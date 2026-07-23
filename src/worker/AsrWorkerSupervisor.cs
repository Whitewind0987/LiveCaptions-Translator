using System.Diagnostics;
using System.IO;
using LiveCaptionsTranslator.ipc;

namespace LiveCaptionsTranslator.worker
{
    public sealed class AsrWorkerSupervisor : IAsyncDisposable
    {
        private readonly string executablePath;
        private readonly IWorkerProcessLauncher launcher;
        private readonly IWorkerJobFactory jobFactory;
        private readonly IAsrWorkerTransportFactory transportFactory;
        private readonly TimeSpan heartbeatInterval;
        private readonly TimeSpan heartbeatTimeout;
        private readonly TimeSpan gracefulShutdownTimeout;
        private readonly SemaphoreSlim lifecycle = new(1, 1);
        private readonly object stateLock = new();
        private AsrWorkerState state = AsrWorkerState.Stopped;
        private AsrWorkerFailureKind failureKind;
        private string? failureReason;
        private WorkerPipeIdentity? identity;
        private IWorkerProcess? process;
        private IWorkerJob? job;
        private IAsrWorkerTransport? transport;
        private CancellationTokenSource? sessionCancellation;
        private Task? heartbeatTask;
        private Task? monitorTask;
        private DateTimeOffset? handshakeStarted;
        private DateTimeOffset? handshakeCompleted;
        private WorkerCapabilities capabilities;
        private ushort negotiatedMinor;
        private long heartbeatFailures;
        private bool gracefulShutdown;
        private bool forcedTermination;
        private int? lastProcessExitCode;
        private bool lastJobAssignmentSucceeded;
        private IReadOnlyList<string> lastStdout = [];
        private IReadOnlyList<string> lastStderr = [];
        private bool disposed;

        public AsrWorkerSupervisor(string executablePath, IWorkerProcessLauncher? launcher = null, IWorkerJobFactory? jobFactory = null, IAsrWorkerTransportFactory? transportFactory = null, TimeSpan? heartbeatInterval = null, TimeSpan? heartbeatTimeout = null, TimeSpan? gracefulShutdownTimeout = null)
        {
            this.executablePath = Path.GetFullPath(executablePath ?? throw new ArgumentNullException(nameof(executablePath)));
            this.launcher = launcher ?? new WorkerProcessLauncher();
            this.jobFactory = jobFactory ?? new WindowsWorkerJobFactory();
            this.transportFactory = transportFactory ?? new NamedPipeWorkerTransportFactory();
            this.heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(2);
            this.heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(6);
            this.gracefulShutdownTimeout = gracefulShutdownTimeout ?? TimeSpan.FromSeconds(5);
        }

        public event EventHandler<AsrWorkerStatus>? StatusChanged;
        public AsrWorkerState State { get { lock (stateLock) return state; } }
        public Guid? SessionId { get { lock (stateLock) return identity?.SessionId; } }
        public int? ProcessId { get { lock (stateLock) return process?.Id; } }
        public IAsrWorkerTransport? ActiveTransport { get { lock (stateLock) return transport; } }
        public AsrWorkerDiagnostics Diagnostics
        {
            get
            {
                lock (stateLock)
                {
                    return new(state, identity?.SessionId, process?.Id, executablePath,
                        handshakeCompleted.HasValue ? $"{IpcProtocol.Major}.{negotiatedMinor}" : null,
                        capabilities, handshakeStarted, handshakeCompleted, transport?.LatestPongAtUtc,
                        heartbeatFailures, transport?.ControlMessagesSent ?? 0, transport?.ControlMessagesReceived ?? 0,
                        0, 0, 0, 0, 0, 0, process?.ExitCode ?? lastProcessExitCode, gracefulShutdown, forcedTermination,
                        job?.AssignmentSucceeded ?? lastJobAssignmentSucceeded, process?.RecentStdout ?? lastStdout, process?.RecentStderr ?? lastStderr, failureKind, failureReason);
                }
            }
        }

        public async Task<AsrWorkerStartResult> StartAsync(CancellationToken cancellationToken = default)
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (state is AsrWorkerState.Ready or AsrWorkerState.Streaming) return AsrWorkerStartResult.Started(identity!.SessionId, process!.Id);
                Publish(SetStateLocked(AsrWorkerState.Starting));
                if (!File.Exists(executablePath)) return FailStartLocked(AsrWorkerFailureKind.WorkerExecutableMissing, $"Worker executable '{executablePath}' was not found.");
                identity = WorkerPipeIdentity.Create();
                transport = transportFactory.Create(identity);
                job = jobFactory.Create();
                sessionCancellation = new CancellationTokenSource();
                try
                {
                    process = await launcher.LaunchAsync(new WorkerLaunchRequest(executablePath, identity.ControlPipeName, identity.AudioPipeName, identity.SessionId, Environment.ProcessId, identity.Nonce), cancellationToken).ConfigureAwait(false);
                    await job.AssignAsync(process, cancellationToken).ConfigureAwait(false);
                    if (!job.AssignmentSucceeded && job.FailureReason != null)
                    {
                        failureKind = AsrWorkerFailureKind.JobAssignmentFailed;
                        failureReason = job.FailureReason;
                    }
                    Publish(SetStateLocked(AsrWorkerState.Connecting));
                    handshakeStarted = DateTimeOffset.UtcNow;
                    Publish(SetStateLocked(AsrWorkerState.Handshaking));
                    var result = await transport.ConnectAndHandshakeAsync(process.Id, cancellationToken).ConfigureAwait(false);
                    negotiatedMinor = result.NegotiatedMinor; capabilities = result.Capabilities; handshakeCompleted = DateTimeOffset.UtcNow;
                    heartbeatTask = HeartbeatLoopAsync(sessionCancellation.Token);
                    monitorTask = MonitorProcessAsync(process, sessionCancellation.Token);
                    var ready = SetStateLocked(AsrWorkerState.Ready);
                    Publish(ready);
                    return AsrWorkerStartResult.Started(identity.SessionId, process.Id);
                }
                catch (Exception ex)
                {
                    var kind = ex switch { FileNotFoundException => AsrWorkerFailureKind.WorkerExecutableMissing, TimeoutException => AsrWorkerFailureKind.ControlPipeTimeout, IpcProtocolException => AsrWorkerFailureKind.HandshakeRejected, _ => AsrWorkerFailureKind.WorkerLaunchFailed };
                    await CleanupOwnedAsync(force: true).ConfigureAwait(false);
                    return FailStartLocked(kind, ex.Message);
                }
            }
            finally { lifecycle.Release(); }
        }

        private AsrWorkerStartResult FailStartLocked(AsrWorkerFailureKind kind, string reason)
        {
            var status = SetStateLocked(kind == AsrWorkerFailureKind.WorkerExecutableMissing ? AsrWorkerState.Unavailable : AsrWorkerState.Faulted, kind, reason); Publish(status); return AsrWorkerStartResult.Failed(status.State, kind, reason);
        }

        public async Task<AsrWorkerStartResult> RestartAsync(CancellationToken cancellationToken = default)
        {
            Publish(SetStateLocked(AsrWorkerState.Restarting));
            await StopAsync(cancellationToken).ConfigureAwait(false);
            return await StartAsync(cancellationToken).ConfigureAwait(false);
        }

        public void MarkStreaming(bool streaming) => Publish(SetStateLocked(streaming ? AsrWorkerState.Streaming : AsrWorkerState.Ready));

        public Task TerminateOwnedWorkerForDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            IWorkerProcess owned;
            lock (stateLock)
                owned = process ?? throw new InvalidOperationException("No owned worker process is running.");
            return owned.TerminateTreeAsync(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (state == AsrWorkerState.Stopped) return;
                Publish(SetStateLocked(AsrWorkerState.Stopping));
                sessionCancellation?.Cancel();
                if (transport != null && identity != null)
                {
                    using var graceful = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    graceful.CancelAfter(gracefulShutdownTimeout);
                    try { gracefulShutdown = await transport.ShutdownAsync(identity.SessionId, graceful.Token).ConfigureAwait(false); }
                    catch (Exception ex) { gracefulShutdown = false; failureReason = $"Graceful shutdown failed: {ex.Message}"; }
                }
                if (process != null && !process.HasExited)
                {
                    try { await process.Completion.WaitAsync(gracefulShutdownTimeout, cancellationToken).ConfigureAwait(false); }
                    catch (TimeoutException) { forcedTermination = true; await process.TerminateTreeAsync(cancellationToken).ConfigureAwait(false); }
                }
                await CleanupOwnedAsync(force: false).ConfigureAwait(false);
                Publish(SetStateLocked(AsrWorkerState.Stopped));
            }
            finally { lifecycle.Release(); }
        }

        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(heartbeatInterval, cancellationToken).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(heartbeatTimeout);
                    await transport!.PingAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) { Interlocked.Increment(ref heartbeatFailures); await RecordUnexpectedFailureAsync(AsrWorkerFailureKind.HeartbeatTimeout, ex.Message).ConfigureAwait(false); }
        }

        private async Task MonitorProcessAsync(IWorkerProcess owned, CancellationToken cancellationToken)
        {
            try { await owned.Completion.ConfigureAwait(false); if (!cancellationToken.IsCancellationRequested) await RecordUnexpectedFailureAsync(AsrWorkerFailureKind.WorkerExited, $"Worker exited with code {owned.ExitCode}.").ConfigureAwait(false); }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { await RecordUnexpectedFailureAsync(AsrWorkerFailureKind.WorkerExited, ex.Message).ConfigureAwait(false); }
        }

        private async Task RecordUnexpectedFailureAsync(AsrWorkerFailureKind kind, string reason)
        {
            if (state is AsrWorkerState.Stopping or AsrWorkerState.Stopped or AsrWorkerState.Faulted) return;
            Publish(SetStateLocked(AsrWorkerState.Faulted, kind, reason));
            sessionCancellation?.Cancel();
            await Task.CompletedTask;
        }

        private async Task CleanupOwnedAsync(bool force)
        {
            var cleanupFailures = new List<string>();
            var oldHeartbeat = heartbeatTask;
            var oldMonitor = monitorTask;
            sessionCancellation?.Cancel();
            if (force && process != null && !process.HasExited)
            {
                forcedTermination = true;
                try { await process.TerminateTreeAsync().ConfigureAwait(false); }
                catch (Exception ex) { cleanupFailures.Add($"Owned process termination failed: {ex.Message}"); }
            }
            if (transport != null)
            {
                try { await transport.StopAsync().ConfigureAwait(false); }
                catch (Exception ex) { cleanupFailures.Add($"Transport stop failed: {ex.Message}"); }
                try { await transport.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { cleanupFailures.Add($"Transport disposal failed: {ex.Message}"); }
            }
            if (process != null) { lastProcessExitCode = process.ExitCode; lastStdout = process.RecentStdout; lastStderr = process.RecentStderr; }
            if (job != null) lastJobAssignmentSucceeded = job.AssignmentSucceeded;
            if (job != null) try { await job.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { cleanupFailures.Add($"Job disposal failed: {ex.Message}"); }
            if (process != null) try { await process.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { cleanupFailures.Add($"Process disposal failed: {ex.Message}"); }
            if (process != null) { lastProcessExitCode = process.ExitCode ?? lastProcessExitCode; lastStdout = process.RecentStdout; lastStderr = process.RecentStderr; }
            if (oldHeartbeat != null) try { await oldHeartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { /* Expected session cancellation. */ } catch (Exception ex) { cleanupFailures.Add($"Heartbeat join failed: {ex.Message}"); }
            if (oldMonitor != null) try { await oldMonitor.ConfigureAwait(false); } catch (OperationCanceledException) { /* Expected session cancellation. */ } catch (Exception ex) { cleanupFailures.Add($"Process monitor join failed: {ex.Message}"); }
            sessionCancellation?.Dispose();
            transport = null; job = null; process = null; sessionCancellation = null; heartbeatTask = null; monitorTask = null;
            if (cleanupFailures.Count != 0)
            {
                failureKind = AsrWorkerFailureKind.CleanupFailed;
                failureReason = string.Join(" | ", new[] { failureReason }.Where(value => !string.IsNullOrWhiteSpace(value)).Concat(cleanupFailures));
            }
        }

        private AsrWorkerStatus SetStateLocked(AsrWorkerState value, AsrWorkerFailureKind kind = AsrWorkerFailureKind.None, string? reason = null)
        {
            lock (stateLock) { state = value; if (kind != AsrWorkerFailureKind.None) { failureKind = kind; failureReason = reason; } return new(value, failureKind, failureReason, DateTimeOffset.UtcNow); }
        }
        private void Publish(AsrWorkerStatus status) { foreach (EventHandler<AsrWorkerStatus> handler in StatusChanged?.GetInvocationList() ?? []) try { handler(this, status); } catch (Exception ex) { Debug.WriteLine($"Worker status subscriber failed: {ex}"); } }

        public async ValueTask DisposeAsync() { if (disposed) return; await StopAsync().ConfigureAwait(false); disposed = true; lifecycle.Dispose(); }
    }
}
