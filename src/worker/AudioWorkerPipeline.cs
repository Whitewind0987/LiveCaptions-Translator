using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.ipc;

namespace LiveCaptionsTranslator.worker
{
    public enum AudioWorkerPipelineState { Stopped, Starting, Streaming, Stopping, Faulted }
    public sealed record AudioWorkerPipelineDiagnostics(AudioWorkerPipelineState State, AudioCaptureDiagnostics Capture, AudioFramePumpDiagnostics? Pump, AudioStreamSummaryPayload? WorkerSummary, AsrWorkerFailureKind FailureKind, string? FailureReason, IReadOnlyList<string> CleanupFailures);

    public sealed class AudioWorkerPipeline : IAsyncDisposable
    {
        private readonly AudioCaptureService capture;
        private readonly AsrWorkerSupervisor supervisor;
        private readonly SemaphoreSlim lifecycle = new(1, 1);
        private readonly object stateLock = new();
        private CancellationTokenSource? sessionCancellation;
        private AudioFramePump? pump;
        private Task? pumpTask;
        private Task? pumpMonitorTask;
        private Task? terminalCleanupTask;
        private AudioFramePumpDiagnostics? lastPumpDiagnostics;
        private Guid captureSessionId;
        private AudioStreamSummaryPayload? summary;
        private AsrWorkerFailureKind failureKind;
        private string? failureReason;
        private readonly List<string> cleanupFailures = [];
        private AudioWorkerPipelineState state;
        private bool disposed;

        public AudioWorkerPipeline(AudioCaptureService capture, AsrWorkerSupervisor supervisor)
        {
            this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
            this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            capture.StatusChanged += OnCaptureStatusChanged;
            supervisor.StatusChanged += OnWorkerStatusChanged;
        }

        public AudioWorkerPipelineState State { get { lock (stateLock) return state; } }
        public AudioWorkerPipelineDiagnostics Diagnostics
        {
            get { lock (stateLock) return new(state, capture.Diagnostics, pump?.Diagnostics ?? lastPumpDiagnostics, summary, failureKind, failureReason, cleanupFailures.ToArray()); }
        }

        public async Task StartAsync(string? endpointId = null, CancellationToken cancellationToken = default)
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            var ownsLifecycle = true;
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (state == AudioWorkerPipelineState.Streaming) return;
                if (terminalCleanupTask is { IsCompleted: false }) throw new InvalidOperationException("Pipeline cleanup is still in progress.");
                state = AudioWorkerPipelineState.Starting;
                summary = null; lastPumpDiagnostics = null; failureKind = AsrWorkerFailureKind.None; failureReason = null; cleanupFailures.Clear(); captureSessionId = Guid.Empty;
                var worker = await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
                if (!worker.Success) throw new WorkerTransportException(worker.FailureKind, worker.FailureReason ?? "Worker failed to start.");
                var captureResult = await capture.StartAsync(endpointId, cancellationToken).ConfigureAwait(false);
                if (!captureResult.Success || captureResult.SessionId == null) throw new WorkerTransportException(AsrWorkerFailureKind.AudioCaptureFailed, captureResult.FailureReason ?? "Audio capture failed to start.");
                captureSessionId = captureResult.SessionId.Value;
                var transport = supervisor.ActiveTransport ?? throw new WorkerTransportException(AsrWorkerFailureKind.ControlPipeClosed, "Worker transport is unavailable.");
                await transport.StartAudioStreamAsync(new StartAudioStreamPayload(worker.SessionId!.Value, captureSessionId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
                sessionCancellation = new CancellationTokenSource();
                pump = new AudioFramePump(capture.FrameBuffer, transport, worker.SessionId.Value, captureSessionId, 1);
                pumpTask = pump.RunAsync(sessionCancellation.Token);
                pumpMonitorTask = MonitorPumpAsync(pumpTask);
                await supervisor.SetStreamingAsync(true, cancellationToken).ConfigureAwait(false);
                state = AudioWorkerPipelineState.Streaming;
            }
            catch (Exception ex)
            {
                var kind = ex is WorkerTransportException transportException ? transportException.Kind : AsrWorkerFailureKind.AudioPumpFailed;
                var cleanup = BeginTerminalCleanup(kind, ex.Message, normalStop: false);
                lifecycle.Release();
                ownsLifecycle = false;
                await cleanup.ConfigureAwait(false);
                throw new AggregateException(new[] { ex }.Concat(cleanupFailures.Select(message => new InvalidOperationException(message))));
            }
            finally
            {
                if (ownsLifecycle) lifecycle.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Task cleanup;
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (state == AudioWorkerPipelineState.Stopped) return;
                if (terminalCleanupTask is { IsCompleted: false }) cleanup = terminalCleanupTask;
                else
                {
                    state = AudioWorkerPipelineState.Stopping;
                    cleanup = terminalCleanupTask = CleanupAsync(normalStop: true, AsrWorkerFailureKind.None, null);
                }
            }
            finally { lifecycle.Release(); }
            await cleanup.ConfigureAwait(false);
            if (State == AudioWorkerPipelineState.Faulted) throw new InvalidOperationException(failureReason);
        }

        private async Task MonitorPumpAsync(Task ownedPump)
        {
            try
            {
                await ownedPump.ConfigureAwait(false);
                if (State == AudioWorkerPipelineState.Streaming)
                    await BeginTerminalCleanup(AsrWorkerFailureKind.AudioPumpFailed, "Audio pump completed while the pipeline was still streaming.", normalStop: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sessionCancellation?.IsCancellationRequested == true) { }
            catch (Exception ex)
            {
                var kind = ex is WorkerTransportException transportException ? transportException.Kind : AsrWorkerFailureKind.AudioPumpFailed;
                await BeginTerminalCleanup(kind, ex.Message, normalStop: false).ConfigureAwait(false);
            }
        }

        private Task BeginTerminalCleanup(AsrWorkerFailureKind kind, string reason, bool normalStop)
        {
            lock (stateLock)
            {
                if (terminalCleanupTask is { IsCompleted: false }) return terminalCleanupTask;
                if (!normalStop) { state = AudioWorkerPipelineState.Faulted; failureKind = kind; failureReason = reason; }
                return terminalCleanupTask = CleanupAsync(normalStop, kind, reason);
            }
        }

        private async Task CleanupAsync(bool normalStop, AsrWorkerFailureKind originalKind, string? originalReason)
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            var failures = new List<string>();
            try
            {
                if (!normalStop) sessionCancellation?.Cancel();
                try { await capture.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { failures.Add($"Capture stop failed: {ex.Message}"); }

                if (pumpTask != null)
                {
                    try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        sessionCancellation?.Cancel();
                        try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                        catch (Exception ex) { failures.Add($"Pump cancellation/join failed: {ex.Message}"); }
                    }
                    catch (OperationCanceledException) when (!normalStop) { }
                    catch (Exception ex) { failures.Add($"Pump failed: {ex.Message}"); }
                }

                lastPumpDiagnostics = pump?.Diagnostics;
                var transport = supervisor.ActiveTransport;
                if (normalStop && failures.Count == 0 && transport != null && supervisor.SessionId != null && captureSessionId != Guid.Empty)
                {
                    try
                    {
                        var diagnostics = lastPumpDiagnostics ?? throw new WorkerTransportException(AsrWorkerFailureKind.AudioPumpFailed, "Pump diagnostics are unavailable.");
                        var end = new AudioStreamEndPayload(
                            supervisor.SessionId.Value,
                            captureSessionId,
                            diagnostics.FramesSent,
                            diagnostics.BytesSent,
                            diagnostics.FirstSequence ?? 0,
                            diagnostics.LastSequence ?? 0,
                            diagnostics.SourceSequenceGaps);
                        ValidateEnd(end, diagnostics);
                        await transport.EndAudioStreamAsync(end, CancellationToken.None).ConfigureAwait(false);
                        summary = await transport.StopAudioStreamAsync(supervisor.SessionId.Value, captureSessionId, CancellationToken.None).ConfigureAwait(false);
                        ValidateSummary(summary, diagnostics);
                    }
                    catch (Exception ex)
                    {
                        if (originalKind == AsrWorkerFailureKind.None && ex is WorkerTransportException transportException)
                        {
                            originalKind = transportException.Kind;
                            originalReason = transportException.Message;
                        }
                        failures.Add($"Stream completion failed: {ex.Message}");
                    }
                }

                try { await supervisor.SetStreamingAsync(false, CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
                try { await supervisor.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { failures.Add($"Worker stop failed: {ex.Message}"); }
                sessionCancellation?.Cancel();
                try { sessionCancellation?.Dispose(); } catch (Exception ex) { failures.Add($"Session cancellation disposal failed: {ex.Message}"); }
                sessionCancellation = null; pumpTask = null; pump = null;

                lock (stateLock)
                {
                    cleanupFailures.AddRange(failures);
                    if (originalKind != AsrWorkerFailureKind.None) { failureKind = originalKind; failureReason = string.Join(" | ", new[] { originalReason }.Concat(failures).Where(value => !string.IsNullOrWhiteSpace(value))); }
                    else if (failures.Count != 0) { failureKind = AsrWorkerFailureKind.CleanupFailed; failureReason = string.Join(" | ", failures); }
                    state = failureKind == AsrWorkerFailureKind.None ? AudioWorkerPipelineState.Stopped : AudioWorkerPipelineState.Faulted;
                }
            }
            finally { lifecycle.Release(); }
        }

        private static void ValidateSummary(AudioStreamSummaryPayload value, AudioFramePumpDiagnostics pumpDiagnostics)
        {
            if (value.FramesReceived != pumpDiagnostics.FramesSent || value.PcmBytesReceived != pumpDiagnostics.BytesSent)
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "Worker frame or byte totals do not match the pump.");
            if (value.FirstSequence != (pumpDiagnostics.FirstSequence ?? 0) || value.LastSequence != (pumpDiagnostics.LastSequence ?? 0))
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "Worker first/last sequence does not match the pump.");
            if (value.SequenceGaps != pumpDiagnostics.SourceSequenceGaps || value.InvalidFrames != 0)
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "Worker gap or invalid-frame totals are inconsistent.");
            if (value.FramesReceived > 0 && (value.FirstTimestampUnixMilliseconds <= 0 || value.LastTimestampUnixMilliseconds < value.FirstTimestampUnixMilliseconds))
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "Worker timestamps are invalid.");
        }

        private static void ValidateEnd(AudioStreamEndPayload value, AudioFramePumpDiagnostics pumpDiagnostics)
        {
            if (value.FramesSent != pumpDiagnostics.FramesSent || value.PcmBytesSent != pumpDiagnostics.BytesSent ||
                value.FirstSequence != (pumpDiagnostics.FirstSequence ?? 0) || value.FinalSequence != (pumpDiagnostics.LastSequence ?? 0) ||
                value.SourceSequenceGaps != pumpDiagnostics.SourceSequenceGaps)
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "AudioStreamEnd totals do not match the pump.");
        }

        private void OnWorkerStatusChanged(object? sender, AsrWorkerStatus status)
        {
            if (status.State is AsrWorkerState.Faulted or AsrWorkerState.Unavailable)
            {
                sessionCancellation?.Cancel();
                BeginTerminalCleanup(status.FailureKind, status.FailureReason ?? "Worker failed.", normalStop: false);
            }
        }

        private void OnCaptureStatusChanged(object? sender, AudioCaptureStatus status)
        {
            if (State == AudioWorkerPipelineState.Streaming && (status.State is AudioCaptureState.Faulted or AudioCaptureState.Unavailable))
            {
                sessionCancellation?.Cancel();
                BeginTerminalCleanup(AsrWorkerFailureKind.AudioCaptureFailed, status.FailureReason ?? "Audio capture failed.", normalStop: false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            var failures = new List<Exception>();
            Task? existingCleanup;
            lock (stateLock) existingCleanup = terminalCleanupTask;
            if (existingCleanup != null) { try { await existingCleanup.ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); } }
            else try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
            capture.StatusChanged -= OnCaptureStatusChanged;
            supervisor.StatusChanged -= OnWorkerStatusChanged;
            if (pumpMonitorTask != null) try { await pumpMonitorTask.ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
            try { await capture.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
            try { await supervisor.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
            disposed = true;
            try { lifecycle.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count != 0) throw new AggregateException("Pipeline disposal failed.", failures);
        }
    }
}
