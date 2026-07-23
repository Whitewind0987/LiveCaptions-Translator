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
        private readonly List<string> cleanupFailures = [];
        private WorkerPipeIdentity? identity;
        private IWorkerProcess? process;
        private IWorkerJob? job;
        private IAsrWorkerTransport? transport;
        private CancellationTokenSource? sessionCancellation;
        private Task? heartbeatTask;
        private Task? processMonitorTask;
        private Task? transportMonitorTask;
        private Task? terminalCleanupTask;
        private bool terminalResourcesReleased;
        private long generation;
        private long diagnosticTerminationGeneration;
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
        private long lastControlSent;
        private long lastControlReceived;
        private long lastAudioFramesSent;
        private long lastAudioBytesSent;
        private long workerFramesReceived;
        private long workerBytesReceived;
        private long sequenceGaps;
        private long workerInvalidFrames;
        private Guid? activeCaptureSessionId;
        private DateTimeOffset? latestProgressAtUtc;
        private DateTimeOffset? lastPongAtUtc;
        private AsrWorkerFailureKind transportFailureKind;
        private string? transportFailureReason;
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
                        capabilities, handshakeStarted, handshakeCompleted, transport?.LatestPongAtUtc ?? lastPongAtUtc,
                        heartbeatFailures, transport?.ControlMessagesSent ?? lastControlSent, transport?.ControlMessagesReceived ?? lastControlReceived,
                        transport?.AudioFramesSent ?? lastAudioFramesSent, transport?.AudioBytesSent ?? lastAudioBytesSent,
                        workerFramesReceived, workerBytesReceived, sequenceGaps, workerInvalidFrames,
                        activeCaptureSessionId, latestProgressAtUtc, transportFailureKind, transportFailureReason,
                        process?.ExitCode ?? lastProcessExitCode, gracefulShutdown, forcedTermination,
                        job?.AssignmentSucceeded ?? lastJobAssignmentSucceeded, process?.RecentStdout ?? lastStdout,
                        process?.RecentStderr ?? lastStderr, failureKind, failureReason, cleanupFailures.ToArray());
                }
            }
        }

        public async Task<AsrWorkerStartResult> StartAsync(CancellationToken cancellationToken = default)
        {
            long currentGeneration;
            AsrWorkerStatus starting;
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (state is AsrWorkerState.Ready or AsrWorkerState.Streaming)
                    return AsrWorkerStartResult.Started(identity!.SessionId, process!.Id);
                if (terminalCleanupTask is { IsCompleted: false })
                    throw new InvalidOperationException("Worker cleanup is still in progress.");
                ResetSessionDiagnosticsLocked();
                currentGeneration = ++generation;
                starting = SetStateLocked(AsrWorkerState.Starting);
            }
            finally { lifecycle.Release(); }
            PublishIfCurrent(starting, currentGeneration);

            AsrWorkerStatus? readyStatus = null;
            AsrWorkerFailureKind startFailure = AsrWorkerFailureKind.None;
            string? startFailureReason = null;
            Guid? startedSession = null;
            int? startedPid = null;
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsCurrentLocked(currentGeneration, AsrWorkerState.Starting))
                    return AsrWorkerStartResult.Failed(State, AsrWorkerFailureKind.CleanupFailed, "Start was invalidated by a reentrant lifecycle operation.");
                if (!File.Exists(executablePath))
                {
                    startFailure = AsrWorkerFailureKind.WorkerExecutableMissing;
                    startFailureReason = $"Worker executable '{executablePath}' was not found.";
                }
                else
                {
                    identity = WorkerPipeIdentity.Create();
                    transport = transportFactory.Create(identity);
                    transport.ProgressReceived += OnProgressReceived;
                    transport.ErrorReceived += OnErrorReceived;
                    job = jobFactory.Create();
                    sessionCancellation = new CancellationTokenSource();
                    try
                    {
                        process = await launcher.LaunchAsync(new WorkerLaunchRequest(executablePath, identity.ControlPipeName, identity.AudioPipeName, identity.SessionId, Environment.ProcessId, identity.Nonce), cancellationToken).ConfigureAwait(false);
                        await job.AssignAsync(process, cancellationToken).ConfigureAwait(false);
                        if (!job.AssignmentSucceeded && job.FailureReason != null) cleanupFailures.Add(job.FailureReason);
                        SetStateLocked(AsrWorkerState.Connecting);
                        handshakeStarted = DateTimeOffset.UtcNow;
                        SetStateLocked(AsrWorkerState.Handshaking);
                        var result = await transport.ConnectAndHandshakeAsync(process.Id, cancellationToken).ConfigureAwait(false);
                        negotiatedMinor = result.NegotiatedMinor;
                        capabilities = result.Capabilities;
                        handshakeCompleted = DateTimeOffset.UtcNow;
                        heartbeatTask = HeartbeatLoopAsync(currentGeneration, sessionCancellation.Token);
                        processMonitorTask = MonitorProcessAsync(currentGeneration, process, sessionCancellation.Token);
                        transportMonitorTask = MonitorTransportAsync(currentGeneration, transport, sessionCancellation.Token);
                        readyStatus = SetStateLocked(AsrWorkerState.Ready);
                        startedSession = identity.SessionId;
                        startedPid = process.Id;
                    }
                    catch (Exception ex)
                    {
                        (startFailure, startFailureReason) = ClassifyStartFailure(ex);
                    }
                }
            }
            finally { lifecycle.Release(); }

            if (startFailure != AsrWorkerFailureKind.None)
            {
                var cleanup = RequestTerminalFailure(currentGeneration, startFailure, startFailureReason!);
                await cleanup.ConfigureAwait(false);
                var failureState = startFailure == AsrWorkerFailureKind.WorkerExecutableMissing ? AsrWorkerState.Unavailable : AsrWorkerState.Faulted;
                return AsrWorkerStartResult.Failed(failureState, startFailure, startFailureReason!);
            }

            PublishIfCurrent(readyStatus!, currentGeneration);
            lock (stateLock)
                return IsCurrentLocked(currentGeneration, AsrWorkerState.Ready)
                    ? AsrWorkerStartResult.Started(startedSession!.Value, startedPid!.Value)
                    : AsrWorkerStartResult.Failed(state, failureKind, failureReason ?? "Start was invalidated by a reentrant lifecycle operation.");
        }

        public async Task<AsrWorkerStartResult> RestartAsync(CancellationToken cancellationToken = default)
        {
            AsrWorkerStatus status;
            lock (stateLock) status = SetStateLocked(AsrWorkerState.Restarting);
            Publish(status);
            await StopAsync(cancellationToken).ConfigureAwait(false);
            return await StartAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task SetStreamingAsync(bool streaming, CancellationToken cancellationToken = default)
        {
            AsrWorkerStatus status;
            long currentGeneration;
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                currentGeneration = generation;
                if (streaming && state != AsrWorkerState.Ready) throw new InvalidOperationException($"Cannot enter Streaming from {state}.");
                if (!streaming && state != AsrWorkerState.Streaming) return;
                status = SetStateLocked(streaming ? AsrWorkerState.Streaming : AsrWorkerState.Ready);
            }
            finally { lifecycle.Release(); }
            PublishIfCurrent(status, currentGeneration);
        }

        public async Task TerminateOwnedWorkerForDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            IWorkerProcess owned;
            long currentGeneration;
            lock (stateLock)
            {
                owned = process ?? throw new InvalidOperationException("No owned worker process is running.");
                currentGeneration = generation;
                diagnosticTerminationGeneration = currentGeneration;
            }
            try { await owned.TerminateTreeAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                lock (stateLock) if (diagnosticTerminationGeneration == currentGeneration) diagnosticTerminationGeneration = 0;
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Task cleanup;
            AsrWorkerStatus? stopping = null;
            long currentGeneration;
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (state == AsrWorkerState.Stopped) return;
                if ((state is AsrWorkerState.Faulted or AsrWorkerState.Unavailable) && terminalResourcesReleased)
                {
                    currentGeneration = generation;
                    stopping = SetStateLocked(AsrWorkerState.Stopped);
                    cleanup = Task.CompletedTask;
                }
                else
                {
                currentGeneration = generation;
                if (terminalCleanupTask is { IsCompleted: false }) cleanup = terminalCleanupTask;
                else
                {
                    stopping = SetStateLocked(AsrWorkerState.Stopping);
                    terminalResourcesReleased = false;
                    cleanup = terminalCleanupTask = CleanupOwnedAsync(null, null);
                }
                }
            }
            finally { lifecycle.Release(); }
            if (stopping != null) PublishIfCurrent(stopping, currentGeneration);
            await cleanup.ConfigureAwait(false);
        }

        private async Task HeartbeatLoopAsync(long sessionGeneration, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(heartbeatInterval, cancellationToken).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(heartbeatTimeout);
                    await transport!.PingAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Interlocked.Increment(ref heartbeatFailures);
                TriggerTerminalFailure(sessionGeneration, AsrWorkerFailureKind.HeartbeatTimeout, ex.Message);
            }
        }

        private async Task MonitorProcessAsync(long sessionGeneration, IWorkerProcess owned, CancellationToken cancellationToken)
        {
            try
            {
                await owned.Completion.ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                    TriggerTerminalFailure(sessionGeneration, AsrWorkerFailureKind.WorkerExited, $"Worker exited with code {owned.ExitCode}.");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                TriggerTerminalFailure(sessionGeneration, AsrWorkerFailureKind.WorkerExited, ex.Message);
            }
        }

        private async Task MonitorTransportAsync(long sessionGeneration, IAsrWorkerTransport owned, CancellationToken cancellationToken)
        {
            try
            {
                var failure = await owned.TerminalFailure.WaitAsync(cancellationToken).ConfigureAwait(false);
                transportFailureKind = failure.Kind;
                transportFailureReason = failure.Reason;
                lock (stateLock) if (diagnosticTerminationGeneration == sessionGeneration) return;
                TriggerTerminalFailure(sessionGeneration, failure.Kind, failure.Reason);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        private Task RequestTerminalFailure(long sessionGeneration, AsrWorkerFailureKind kind, string reason)
        {
            TriggerTerminalFailure(sessionGeneration, kind, reason);
            lock (stateLock) return terminalCleanupTask ?? Task.CompletedTask;
        }

        private void TriggerTerminalFailure(long sessionGeneration, AsrWorkerFailureKind kind, string reason)
        {
            lock (stateLock)
            {
                if (sessionGeneration != generation || terminalCleanupTask is { IsCompleted: false }) return;
                failureKind = kind;
                failureReason = reason;
                state = kind == AsrWorkerFailureKind.WorkerExecutableMissing ? AsrWorkerState.Unavailable : AsrWorkerState.Faulted;
                generation++;
                terminalCleanupTask = CleanupOwnedAsync(kind, reason);
            }
        }

        private async Task CleanupOwnedAsync(AsrWorkerFailureKind? terminalKind, string? terminalReason)
        {
            IAsrWorkerTransport? ownedTransport;
            IWorkerProcess? ownedProcess;
            IWorkerJob? ownedJob;
            CancellationTokenSource? ownedCancellation;
            Task? ownedHeartbeat;
            Task? ownedProcessMonitor;
            Task? ownedTransportMonitor;
            WorkerPipeIdentity? ownedIdentity;

            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                ownedTransport = transport;
                ownedProcess = process;
                ownedJob = job;
                ownedCancellation = sessionCancellation;
                ownedHeartbeat = heartbeatTask;
                ownedProcessMonitor = processMonitorTask;
                ownedTransportMonitor = transportMonitorTask;
                ownedIdentity = identity;
                ownedCancellation?.Cancel();
                if (ownedTransport != null)
                {
                    ownedTransport.ProgressReceived -= OnProgressReceived;
                    ownedTransport.ErrorReceived -= OnErrorReceived;
                    lastControlSent = ownedTransport.ControlMessagesSent;
                    lastControlReceived = ownedTransport.ControlMessagesReceived;
                    lastAudioFramesSent = ownedTransport.AudioFramesSent;
                    lastAudioBytesSent = ownedTransport.AudioBytesSent;
                    lastPongAtUtc = ownedTransport.LatestPongAtUtc;
                }
                transport = null; process = null; job = null; sessionCancellation = null;
                heartbeatTask = null; processMonitorTask = null; transportMonitorTask = null; identity = null;
            }
            finally { lifecycle.Release(); }

            var failures = new List<string>();
            if (ownedTransport != null && ownedIdentity != null && ownedProcess is { HasExited: false })
            {
                using var graceful = new CancellationTokenSource(gracefulShutdownTimeout);
                try { gracefulShutdown = await ownedTransport.ShutdownAsync(ownedIdentity.SessionId, graceful.Token).ConfigureAwait(false); }
                catch (Exception ex) { gracefulShutdown = false; failures.Add($"Graceful shutdown failed: {ex.Message}"); }
            }
            if (ownedProcess is { HasExited: false })
            {
                try { await ownedProcess.Completion.WaitAsync(gracefulShutdownTimeout).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    forcedTermination = true;
                    try { await ownedProcess.TerminateTreeAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { failures.Add($"Owned process termination failed: {ex.Message}"); }
                }
            }
            if (ownedTransport != null) try { await ownedTransport.StopAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add($"Transport stop failed: {ex.Message}"); }
            if (ownedTransport != null) try { await ownedTransport.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add($"Transport disposal failed: {ex.Message}"); }
            if (ownedJob != null) { lastJobAssignmentSucceeded = ownedJob.AssignmentSucceeded; try { await ownedJob.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add($"Job disposal failed: {ex.Message}"); } }
            if (ownedProcess != null)
            {
                lastProcessExitCode = ownedProcess.ExitCode;
                lastStdout = ownedProcess.RecentStdout;
                lastStderr = ownedProcess.RecentStderr;
                try { await ownedProcess.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add($"Process disposal failed: {ex.Message}"); }
                lastProcessExitCode = ownedProcess.ExitCode ?? lastProcessExitCode;
                lastStdout = ownedProcess.RecentStdout;
                lastStderr = ownedProcess.RecentStderr;
            }
            await JoinAsync(ownedHeartbeat, "Heartbeat join", failures).ConfigureAwait(false);
            await JoinAsync(ownedProcessMonitor, "Process monitor join", failures).ConfigureAwait(false);
            await JoinAsync(ownedTransportMonitor, "Transport monitor join", failures).ConfigureAwait(false);
            try { ownedCancellation?.Dispose(); } catch (Exception ex) { failures.Add($"Session cancellation disposal failed: {ex.Message}"); }

            AsrWorkerStatus terminalStatus;
            lock (stateLock)
            {
                cleanupFailures.AddRange(failures);
                if (terminalKind.HasValue)
                {
                    failureKind = terminalKind.Value;
                    failureReason = string.Join(" | ", new[] { terminalReason }.Concat(cleanupFailures).Where(value => !string.IsNullOrWhiteSpace(value)));
                    terminalStatus = SetStateLocked(terminalKind == AsrWorkerFailureKind.WorkerExecutableMissing ? AsrWorkerState.Unavailable : AsrWorkerState.Faulted, terminalKind.Value, failureReason);
                }
                else
                {
                    if (failures.Count != 0 && failureKind == AsrWorkerFailureKind.None) { failureKind = AsrWorkerFailureKind.CleanupFailed; failureReason = string.Join(" | ", failures); }
                    terminalStatus = SetStateLocked(AsrWorkerState.Stopped);
                }
                terminalResourcesReleased = true;
            }
            Publish(terminalStatus);
        }

        private static async Task JoinAsync(Task? task, string phase, List<string> failures)
        {
            if (task == null) return;
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failures.Add($"{phase} failed: {ex.Message}"); }
        }

        private void OnProgressReceived(object? sender, AudioStreamSummaryPayload progress)
        {
            lock (stateLock)
            {
                activeCaptureSessionId = progress.CaptureSessionId;
                workerFramesReceived = progress.FramesReceived;
                workerBytesReceived = progress.PcmBytesReceived;
                sequenceGaps = progress.SequenceGaps;
                workerInvalidFrames = progress.InvalidFrames;
                latestProgressAtUtc = DateTimeOffset.UtcNow;
            }
        }

        private void OnErrorReceived(object? sender, ErrorPayload error)
        {
            lock (stateLock) transportFailureReason = $"Worker error {error.Kind}: {error.Diagnostic}";
        }

        private (AsrWorkerFailureKind Kind, string Reason) ClassifyStartFailure(Exception exception) => exception switch
        {
            WorkerTransportException transportException => (transportException.Kind, transportException.Message),
            FileNotFoundException => (AsrWorkerFailureKind.WorkerExecutableMissing, exception.Message),
            OperationCanceledException => (AsrWorkerFailureKind.WorkerLaunchFailed, exception.Message),
            _ => (AsrWorkerFailureKind.WorkerLaunchFailed, exception.Message)
        };

        private void ResetSessionDiagnosticsLocked()
        {
            failureKind = AsrWorkerFailureKind.None; failureReason = null; cleanupFailures.Clear();
            handshakeStarted = null; handshakeCompleted = null; capabilities = WorkerCapabilities.None; negotiatedMinor = 0;
            heartbeatFailures = 0; gracefulShutdown = false; forcedTermination = false;
            workerFramesReceived = 0; workerBytesReceived = 0; sequenceGaps = 0; workerInvalidFrames = 0;
            activeCaptureSessionId = null; latestProgressAtUtc = null; transportFailureKind = AsrWorkerFailureKind.None; transportFailureReason = null;
            lastPongAtUtc = null;
            lastControlSent = 0; lastControlReceived = 0; lastAudioFramesSent = 0; lastAudioBytesSent = 0;
            terminalResourcesReleased = false;
            diagnosticTerminationGeneration = 0;
        }

        private bool IsCurrentLocked(long expectedGeneration, AsrWorkerState expectedState) { lock (stateLock) return generation == expectedGeneration && state == expectedState; }
        private AsrWorkerStatus SetStateLocked(AsrWorkerState value, AsrWorkerFailureKind kind = AsrWorkerFailureKind.None, string? reason = null)
        {
            lock (stateLock)
            {
                state = value;
                if (kind != AsrWorkerFailureKind.None) { failureKind = kind; failureReason = reason; }
                return new(value, failureKind, failureReason, DateTimeOffset.UtcNow);
            }
        }

        private void PublishIfCurrent(AsrWorkerStatus status, long expectedGeneration)
        {
            lock (stateLock) if (generation != expectedGeneration || state != status.State) return;
            Publish(status);
        }

        private void Publish(AsrWorkerStatus status)
        {
            foreach (EventHandler<AsrWorkerStatus> handler in StatusChanged?.GetInvocationList() ?? [])
                try { handler(this, status); } catch (Exception ex) { Debug.WriteLine($"Worker status subscriber failed: {ex}"); }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            disposed = true;
        }
    }
}
