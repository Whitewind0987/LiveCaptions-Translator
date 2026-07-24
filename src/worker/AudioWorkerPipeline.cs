using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.ipc;
using LiveCaptionsTranslator.captioning;

namespace LiveCaptionsTranslator.worker
{
    public enum AudioWorkerPipelineState { Stopped, Starting, Streaming, Stopping, Faulted }
    public sealed record AudioWorkerPipelineDiagnostics(AudioWorkerPipelineState State, AudioCaptureDiagnostics Capture, AudioFramePumpDiagnostics? Pump, bool PumpJoined, AudioStreamSummaryPayload? WorkerSummary, AsrWorkerFailureKind FailureKind, string? FailureReason, IReadOnlyList<string> CleanupFailures);
    internal sealed record AudioPumpDrainOptions(TimeSpan StallTimeout, TimeSpan OverallTimeout, TimeSpan PollInterval)
    {
        public static AudioPumpDrainOptions Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100));
    }

    public sealed class AudioWorkerPipeline : IAsyncDisposable
    {
        private readonly AudioCaptureService capture;
        private readonly AsrWorkerSupervisor supervisor;
        private readonly SemaphoreSlim lifecycle = new(1, 1);
        private readonly object stateLock = new();
        private readonly AudioPumpDrainOptions drainOptions;
        private readonly Func<Task>? captureStopOverride;
        private CancellationTokenSource? sessionCancellation;
        private AudioFramePump? pump;
        private Task? pumpTask;
        private Task? pumpMonitorTask;
        private Task? terminalCleanupTask;
        private AudioFramePumpDiagnostics? lastPumpDiagnostics;
        private bool pumpJoined;
        private Guid captureSessionId;
        private AudioStreamSummaryPayload? summary;
        private AsrWorkerFailureKind failureKind;
        private string? failureReason;
        private readonly List<string> cleanupFailures = [];
        private AudioWorkerPipelineState state;
        private bool disposed;
        private IAsrWorkerTransport? captionTransport;
        private long captionGeneration;
        private bool captionDeliveryEnabled;
        private int captionPublications;
        private TaskCompletionSource captionPublicationsChanged = NewSignal();
        private readonly AsyncLocal<int> captionPublicationDepth = new();
        private readonly Queue<(CaptionEvent Event, long Generation)> captionQueue = [];
        private bool captionDispatcherScheduled;

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AudioWorkerPipeline(AudioCaptureService capture, AsrWorkerSupervisor supervisor)
            : this(capture, supervisor, AudioPumpDrainOptions.Default, null)
        {
        }

        internal AudioWorkerPipeline(
            AudioCaptureService capture,
            AsrWorkerSupervisor supervisor,
            AudioPumpDrainOptions drainOptions,
            Func<Task>? captureStopOverride)
        {
            this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
            this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            this.drainOptions = drainOptions ?? throw new ArgumentNullException(nameof(drainOptions));
            this.captureStopOverride = captureStopOverride;
            capture.StatusChanged += OnCaptureStatusChanged;
            supervisor.StatusChanged += OnWorkerStatusChanged;
        }

        public AudioWorkerPipelineState State { get { lock (stateLock) return state; } }
        public event EventHandler<CaptionEvent>? CaptionEventReceived;
        public AudioWorkerPipelineDiagnostics Diagnostics
        {
            get { lock (stateLock) return new(state, capture.Diagnostics, pump?.Diagnostics ?? lastPumpDiagnostics, pumpJoined, summary, failureKind, failureReason, cleanupFailures.ToArray()); }
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
                summary = null; lastPumpDiagnostics = null; pumpJoined = false; failureKind = AsrWorkerFailureKind.None; failureReason = null; cleanupFailures.Clear(); captureSessionId = Guid.Empty;
                var worker = await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
                if (!worker.Success) throw new WorkerTransportException(worker.FailureKind, worker.FailureReason ?? "Worker failed to start.");
                var captureResult = await capture.StartAsync(endpointId, cancellationToken).ConfigureAwait(false);
                if (!captureResult.Success || captureResult.SessionId == null) throw new WorkerTransportException(AsrWorkerFailureKind.AudioCaptureFailed, captureResult.FailureReason ?? "Audio capture failed to start.");
                captureSessionId = captureResult.SessionId.Value;
                var transport = supervisor.ActiveTransport ?? throw new WorkerTransportException(AsrWorkerFailureKind.ControlPipeClosed, "Worker transport is unavailable.");
                lock (stateLock)
                {
                    captionTransport = transport;
                    captionDeliveryEnabled = true;
                    captionGeneration++;
                }
                transport.CaptionEventReceived += OnCaptionEventReceived;
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
            await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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
                var worker = supervisor.Diagnostics;
                var kind = worker.State == AsrWorkerState.Faulted && worker.FailureKind == AsrWorkerFailureKind.WorkerExited
                    ? AsrWorkerFailureKind.WorkerExited
                    : ex is WorkerTransportException transportException ? transportException.Kind : AsrWorkerFailureKind.AudioPumpFailed;
                var reason = kind == AsrWorkerFailureKind.WorkerExited ? worker.FailureReason ?? ex.Message : ex.Message;
                await BeginTerminalCleanup(kind, reason, normalStop: false).ConfigureAwait(false);
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
            var pumpDrainedNormally = pumpTask == null;
            try
            {
                if (!normalStop) InvalidateCaptionDelivery();
                if (!normalStop) RequestPumpCancellation(originalReason ?? "Terminal pipeline cleanup requested pump cancellation.");
                try
                {
                    if (captureStopOverride != null) await captureStopOverride().ConfigureAwait(false);
                    else await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { failures.Add($"Capture stop failed: {ex.Message}"); }

                if (pumpTask != null)
                {
                    var drain = await DrainPumpAsync(pumpTask, normalStop, originalKind).ConfigureAwait(false);
                    pumpJoined = pumpTask.IsCompleted;
                    pumpDrainedNormally = drain.CompletedNormally;
                    if (!drain.CompletedNormally)
                    {
                        if (originalKind == AsrWorkerFailureKind.None)
                        {
                            originalKind = drain.FailureKind;
                            originalReason = drain.FailureReason;
                        }
                        if (!string.IsNullOrWhiteSpace(drain.CleanupFailure))
                            failures.Add(drain.CleanupFailure);
                    }
                }

                lastPumpDiagnostics = pump?.Diagnostics;
                var transport = supervisor.ActiveTransport;
                if (normalStop && pumpDrainedNormally && failures.Count == 0 && transport != null && supervisor.SessionId != null && captureSessionId != Guid.Empty)
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

                if (normalStop) await DrainAndDisableCaptionDeliveryAsync().ConfigureAwait(false);

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

        private void OnCaptionEventReceived(object? sender, CaptionEvent captionEvent)
        {
            long generation;
            var overflow = false;
            var schedule = false;
            lock (stateLock)
            {
                if (!captionDeliveryEnabled || captionEvent.SessionId != captureSessionId) return;
                generation = captionGeneration;
                captionPublications++;
                if (captionQueue.Count >= 64)
                {
                    captionPublications--;
                    overflow = true;
                }
                else
                {
                    captionQueue.Enqueue((captionEvent, generation));
                    if (!captionDispatcherScheduled) { captionDispatcherScheduled = true; schedule = true; }
                }
            }
            if (overflow)
            {
                _ = BeginTerminalCleanup(AsrWorkerFailureKind.ProtocolViolation,
                    "Pipeline caption publication queue exceeded its bounded capacity.", normalStop: false);
            }
            else if (schedule) ThreadPool.QueueUserWorkItem(_ => DrainCaptionQueue());
        }

        private void DrainCaptionQueue()
        {
            while (true)
            {
                (CaptionEvent Event, long Generation) publication;
                lock (stateLock)
                {
                    if (captionQueue.Count == 0) { captionDispatcherScheduled = false; return; }
                    publication = captionQueue.Dequeue();
                }
                PublishCaptionEvent(publication.Event, publication.Generation);
            }
        }

        private void PublishCaptionEvent(CaptionEvent captionEvent, long generation)
        {
            captionPublicationDepth.Value++;
            try
            {
                foreach (EventHandler<CaptionEvent> handler in CaptionEventReceived?.GetInvocationList() ?? [])
                {
                    lock (stateLock)
                    {
                        if (!captionDeliveryEnabled || captionGeneration != generation || captionEvent.SessionId != captureSessionId) return;
                    }
                    try { handler(this, captionEvent); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Pipeline caption subscriber failed: {ex}"); }
                }
            }
            finally
            {
                captionPublicationDepth.Value--;
                lock (stateLock)
                {
                    captionPublications--;
                    if (captionPublications == 0)
                    {
                        captionPublicationsChanged.TrySetResult();
                        captionPublicationsChanged = NewSignal();
                    }
                }
            }
        }

        private void InvalidateCaptionDelivery()
        {
            IAsrWorkerTransport? subscribedTransport;
            lock (stateLock)
            {
                captionDeliveryEnabled = false;
                captionGeneration++;
                subscribedTransport = captionTransport;
                captionTransport = null;
            }
            if (subscribedTransport != null) subscribedTransport.CaptionEventReceived -= OnCaptionEventReceived;
        }

        private async Task DrainAndDisableCaptionDeliveryAsync()
        {
            if (captionPublicationDepth.Value != 0)
            {
                InvalidateCaptionDelivery();
                return;
            }

            IAsrWorkerTransport? subscribedTransport = null;

            while (true)
            {
                Task wait;
                lock (stateLock)
                {
                    if (captionPublications == 0)
                    {
                        captionDeliveryEnabled = false;
                        captionGeneration++;
                        subscribedTransport = captionTransport;
                        captionTransport = null;
                        break;
                    }

                    wait = captionPublicationsChanged.Task;
                }

                await wait.ConfigureAwait(false);
            }

            if (subscribedTransport != null) subscribedTransport.CaptionEventReceived -= OnCaptionEventReceived;
        }

        private async Task<PumpDrainResult> DrainPumpAsync(
            Task ownedPump,
            bool normalStop,
            AsrWorkerFailureKind originalKind)
        {
            if (!normalStop)
            {
                try { await ownedPump.ConfigureAwait(false); }
                catch (OperationCanceledException) when (sessionCancellation?.IsCancellationRequested == true) { }
                catch (Exception ex)
                {
                    var kind = ex is WorkerTransportException transportException ? transportException.Kind : AsrWorkerFailureKind.AudioPumpFailed;
                    var cleanupFailure = originalKind == AsrWorkerFailureKind.WorkerExited && kind == AsrWorkerFailureKind.AudioPipeClosed
                        ? null
                        : $"Pump failed: {ex.Message}";
                    return new(false, kind, ex.Message, cleanupFailure);
                }
                return new(false, AsrWorkerFailureKind.AudioPumpFailed, "Audio pump was canceled during terminal cleanup.", null);
            }

            var started = System.Diagnostics.Stopwatch.StartNew();
            var lastProgressAt = started.Elapsed;
            var diagnostics = pump?.Diagnostics;
            var lastFrames = diagnostics?.FramesSent ?? 0;
            var lastBytes = diagnostics?.BytesSent ?? 0;

            while (!ownedPump.IsCompleted)
            {
                var remainingOverall = drainOptions.OverallTimeout - started.Elapsed;
                var remainingStall = drainOptions.StallTimeout - (started.Elapsed - lastProgressAt);
                if (remainingOverall <= TimeSpan.Zero || remainingStall <= TimeSpan.Zero)
                    break;

                var delay = new[] { drainOptions.PollInterval, remainingOverall, remainingStall }.Min();
                await Task.WhenAny(ownedPump, Task.Delay(delay, CancellationToken.None)).ConfigureAwait(false);
                diagnostics = pump?.Diagnostics;
                if (diagnostics != null && (diagnostics.FramesSent > lastFrames || diagnostics.BytesSent > lastBytes))
                {
                    lastFrames = diagnostics.FramesSent;
                    lastBytes = diagnostics.BytesSent;
                    lastProgressAt = started.Elapsed;
                }
            }

            if (ownedPump.IsCompleted)
            {
                try
                {
                    await ownedPump.ConfigureAwait(false);
                    diagnostics = pump?.Diagnostics;
                    if (diagnostics?.Phase == AudioFramePumpPhase.Completed && diagnostics.SourceCompletionObserved)
                        return new(true, AsrWorkerFailureKind.None, null, null);
                    var incomplete = $"Audio pump ended without observing source completion (phase {diagnostics?.Phase}).";
                    return new(false, AsrWorkerFailureKind.AudioPumpFailed, incomplete, null);
                }
                catch (Exception ex)
                {
                    var kind = ex is WorkerTransportException transportException ? transportException.Kind : AsrWorkerFailureKind.AudioPumpFailed;
                    return new(false, kind, ex.Message, null);
                }
            }

            diagnostics = pump?.Diagnostics;
            var timedOutOverall = started.Elapsed >= drainOptions.OverallTimeout;
            var reason = diagnostics?.Phase switch
            {
                AudioFramePumpPhase.WaitingForFrame =>
                    $"Audio pump stalled waiting for source completion after capture stop ({(timedOutOverall ? "overall" : "progress")} timeout).",
                AudioFramePumpPhase.WritingFrame =>
                    $"Audio pump stalled writing frame {diagnostics.CurrentSequence?.ToString() ?? "unknown"} to the worker ({(timedOutOverall ? "overall" : "progress")} timeout).",
                _ => $"Audio pump stalled in phase {diagnostics?.Phase.ToString() ?? "unknown"} ({(timedOutOverall ? "overall" : "progress")} timeout)."
            };

            RequestPumpCancellation(reason);
            try { await ownedPump.ConfigureAwait(false); }
            catch (OperationCanceledException) when (sessionCancellation?.IsCancellationRequested == true) { }
            catch (Exception ex) { return new(false, AsrWorkerFailureKind.AudioPumpFailed, reason, $"{reason} Pump join failed: {ex.Message}"); }
            return new(false, AsrWorkerFailureKind.AudioPumpFailed, reason, null);
        }

        private sealed record PumpDrainResult(bool CompletedNormally, AsrWorkerFailureKind FailureKind, string? FailureReason, string? CleanupFailure);

        private void RequestPumpCancellation(string reason)
        {
            if (pumpTask is { IsCompleted: false })
                pump?.MarkOwnedCancellationRequested(reason);
            sessionCancellation?.Cancel();
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
                RequestPumpCancellation(status.FailureReason ?? "Worker terminal state requested pump cancellation.");
                BeginTerminalCleanup(status.FailureKind, status.FailureReason ?? "Worker failed.", normalStop: false);
            }
        }

        private void OnCaptureStatusChanged(object? sender, AudioCaptureStatus status)
        {
            if (State == AudioWorkerPipelineState.Streaming && (status.State is AudioCaptureState.Faulted or AudioCaptureState.Unavailable))
            {
                RequestPumpCancellation(status.FailureReason ?? "Capture terminal state requested pump cancellation.");
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
