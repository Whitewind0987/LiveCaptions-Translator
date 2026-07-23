using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.ipc;

namespace LiveCaptionsTranslator.worker
{
    public enum AudioWorkerPipelineState { Stopped, Starting, Streaming, Stopping, Faulted }
    public sealed record AudioWorkerPipelineDiagnostics(AudioWorkerPipelineState State, AudioCaptureDiagnostics Capture, AudioFramePumpDiagnostics? Pump, AudioStreamSummaryPayload? WorkerSummary, string? FailureReason);

    public sealed class AudioWorkerPipeline : IAsyncDisposable
    {
        private readonly AudioCaptureService capture;
        private readonly AsrWorkerSupervisor supervisor;
        private readonly SemaphoreSlim lifecycle = new(1, 1);
        private readonly object failureLock = new();
        private CancellationTokenSource? sessionCancellation;
        private AudioFramePump? pump;
        private Task? pumpTask;
        private AudioFramePumpDiagnostics? lastPumpDiagnostics;
        private Guid captureSessionId;
        private AudioStreamSummaryPayload? summary;
        private string? failureReason;
        private Task? peerFailureCleanup;
        private bool disposed;
        public AudioWorkerPipeline(AudioCaptureService capture, AsrWorkerSupervisor supervisor)
        {
            this.capture = capture;
            this.supervisor = supervisor;
            capture.StatusChanged += OnCaptureStatusChanged;
            supervisor.StatusChanged += OnWorkerStatusChanged;
        }
        public AudioWorkerPipelineState State { get; private set; }
        public AudioWorkerPipelineDiagnostics Diagnostics => new(State, capture.Diagnostics, pump?.Diagnostics ?? lastPumpDiagnostics, summary, failureReason);

        public async Task StartAsync(string? endpointId = null, CancellationToken cancellationToken = default)
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (State == AudioWorkerPipelineState.Streaming) return;
                State = AudioWorkerPipelineState.Starting;
                lock (failureLock) peerFailureCleanup = null;
                summary = null;
                lastPumpDiagnostics = null;
                failureReason = null;
                var worker = await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
                if (!worker.Success) throw new InvalidOperationException(worker.FailureReason);
                var captureResult = await capture.StartAsync(endpointId, cancellationToken).ConfigureAwait(false);
                if (!captureResult.Success || captureResult.SessionId == null) throw new InvalidOperationException(captureResult.FailureReason);
                captureSessionId = captureResult.SessionId.Value;
                var transport = supervisor.ActiveTransport ?? throw new InvalidOperationException("Worker transport is unavailable.");
                await transport.StartAudioStreamAsync(new StartAudioStreamPayload(worker.SessionId!.Value, captureSessionId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
                sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pump = new AudioFramePump(capture.FrameBuffer, transport, worker.SessionId.Value, captureSessionId);
                pumpTask = pump.RunAsync(sessionCancellation.Token);
                supervisor.MarkStreaming(true);
                State = AudioWorkerPipelineState.Streaming;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message; State = AudioWorkerPipelineState.Faulted;
                var failures = new List<Exception> { ex };
                try { await capture.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception cleanup) { failures.Add(cleanup); }
                try { await supervisor.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception cleanup) { failures.Add(cleanup); }
                failureReason = string.Join(" | ", failures.Select(failure => failure.Message));
                throw new AggregateException(failures);
            }
            finally { lifecycle.Release(); }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State == AudioWorkerPipelineState.Stopped) return;
                State = AudioWorkerPipelineState.Stopping;
                var failures = new List<Exception>();
                Task? failureCleanup;
                lock (failureLock) failureCleanup = peerFailureCleanup;
                if (failureCleanup != null) try { await failureCleanup.ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
                try { await capture.StopAsync(cancellationToken).ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
                if (pumpTask != null) try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); sessionCancellation?.Cancel(); }
                var transport = supervisor.ActiveTransport;
                if (transport != null && supervisor.SessionId != null && captureSessionId != Guid.Empty)
                    try { summary = await transport.StopAudioStreamAsync(supervisor.SessionId.Value, captureSessionId, cancellationToken).ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
                supervisor.MarkStreaming(false);
                try { await supervisor.StopAsync(cancellationToken).ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
                lastPumpDiagnostics = pump?.Diagnostics;
                sessionCancellation?.Cancel(); sessionCancellation?.Dispose(); sessionCancellation = null; pumpTask = null; pump = null;
                if (failures.Count != 0) { failureReason = string.Join(" | ", failures.Select(x => x.Message)); State = AudioWorkerPipelineState.Faulted; throw new AggregateException(failures); }
                State = AudioWorkerPipelineState.Stopped;
            }
            finally { lifecycle.Release(); }
        }

        private void OnWorkerStatusChanged(object? sender, AsrWorkerStatus status)
        {
            if (status.State is not (AsrWorkerState.Faulted or AsrWorkerState.Unavailable))
                return;
            sessionCancellation?.Cancel();
            lock (failureLock)
                peerFailureCleanup ??= StopCaptureAfterWorkerFailureAsync(status.FailureReason);
        }

        private async Task StopCaptureAfterWorkerFailureAsync(string? reason)
        {
            try { await capture.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { failureReason = $"Worker failure '{reason}' and capture cleanup failed: {ex.Message}"; }
        }

        private void OnCaptureStatusChanged(object? sender, AudioCaptureStatus status)
        {
            if (State != AudioWorkerPipelineState.Streaming || status.State is not (AudioCaptureState.Faulted or AudioCaptureState.Unavailable))
                return;
            sessionCancellation?.Cancel();
            lock (failureLock)
                peerFailureCleanup ??= StopWorkerAfterCaptureFailureAsync(status.FailureReason);
        }

        private async Task StopWorkerAfterCaptureFailureAsync(string? reason)
        {
            try { await supervisor.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { failureReason = $"Capture failure '{reason}' and worker cleanup failed: {ex.Message}"; }
        }

        public async ValueTask DisposeAsync() { if (disposed) return; try { await StopAsync().ConfigureAwait(false); } finally { disposed = true; capture.StatusChanged -= OnCaptureStatusChanged; supervisor.StatusChanged -= OnWorkerStatusChanged; await capture.DisposeAsync().ConfigureAwait(false); await supervisor.DisposeAsync().ConfigureAwait(false); lifecycle.Dispose(); } }
    }
}
