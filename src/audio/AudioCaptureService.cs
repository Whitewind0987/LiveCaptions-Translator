using LiveCaptionsTranslator.audio.buffering;
using LiveCaptionsTranslator.audio.processing;

namespace LiveCaptionsTranslator.audio
{
    public sealed class AudioCaptureService : IAsyncDisposable
    {
        private readonly IAudioEndpointProvider endpointProvider;
        private readonly IAudioCaptureRuntimeFactory runtimeFactory;
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);
        private readonly object stateLock = new();
        private readonly object callbackGate = new();

        private AudioCaptureState state = AudioCaptureState.Stopped;
        private IAudioCaptureRuntime? runtime;
        private StreamingAudioNormalizer? normalizer;
        private AudioFrameAssembler? assembler;
        private BoundedAudioFrameBuffer frameBuffer = new();
        private EventHandler<AudioCaptureData>? dataHandler;
        private EventHandler<AudioRuntimeStopped>? stoppedHandler;
        private Guid? sessionId;
        private AudioEndpointInfo? resolvedEndpoint;
        private AudioInputFormat? inputFormat;
        private string? requestedEndpointId;
        private string? failureReason;
        private string? startDiagnostic;
        private bool usedFallback;
        private bool acceptCallbacks;
        private long framesProduced;
        private long inputBytesReceived;
        private long normalizedBytesProduced;
        private long lastFrameSequence;
        private DateTimeOffset? startedAtUtc;
        private DateTimeOffset? lastFrameAtUtc;
        private int disposeStarted;

        public AudioCaptureService(
            IAudioEndpointProvider endpointProvider,
            IAudioCaptureRuntimeFactory runtimeFactory)
        {
            this.endpointProvider = endpointProvider ?? throw new ArgumentNullException(nameof(endpointProvider));
            this.runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        }

        public AudioCaptureState State { get { lock (stateLock) return state; } }
        public string? FailureReason { get { lock (stateLock) return failureReason; } }
        public BoundedAudioFrameBuffer FrameBuffer { get { lock (stateLock) return frameBuffer; } }

        public AudioCaptureDiagnostics Diagnostics
        {
            get
            {
                lock (stateLock)
                {
                    return new AudioCaptureDiagnostics(
                        sessionId,
                        state,
                        requestedEndpointId,
                        resolvedEndpoint?.Id,
                        resolvedEndpoint?.DisplayName,
                        usedFallback,
                        inputFormat,
                        NormalizedAudioFormat.Description,
                        framesProduced,
                        frameBuffer.ConsumedCount,
                        frameBuffer.Count,
                        frameBuffer.DroppedCount,
                        inputBytesReceived,
                        normalizedBytesProduced,
                        lastFrameSequence,
                        failureReason,
                        startedAtUtc,
                        lastFrameAtUtc);
                }
            }
        }

        public event EventHandler<AudioCaptureStatus>? StatusChanged;
        public event EventHandler<NormalizedAudioFrame>? FrameProduced;

        public async Task<AudioCaptureStartResult> StartAsync(
            string? savedEndpointId,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (State == AudioCaptureState.Running)
                {
                    lock (stateLock)
                    {
                        return AudioCaptureStartResult.Started(
                            sessionId!.Value,
                            resolvedEndpoint!,
                            usedFallback,
                            startDiagnostic);
                    }
                }

                if (runtime != null)
                    await StopCoreAsync().ConfigureAwait(false);

                SetStatus(AudioCaptureState.Starting, null);
                requestedEndpointId = NormalizeEndpointId(savedEndpointId);
                AudioEndpointResolution resolution;
                try
                {
                    resolution = await endpointProvider.ResolveAsync(requestedEndpointId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    SetStatus(AudioCaptureState.Stopped, null);
                    throw;
                }
                if (!resolution.Success || resolution.Endpoint == null)
                {
                    var reason = resolution.FailureReason ?? "No active render endpoint is available.";
                    SetStatus(AudioCaptureState.Unavailable, reason);
                    return AudioCaptureStartResult.Failed(AudioCaptureState.Unavailable, reason);
                }

                var newRuntime = runtimeFactory.Create();
                var newSessionId = Guid.NewGuid();
                EventHandler<AudioCaptureData> onData = (_, data) =>
                    OnDataAvailable(newRuntime, newSessionId, data);
                EventHandler<AudioRuntimeStopped> onStopped = (_, stopped) =>
                    OnRuntimeStopped(newRuntime, newSessionId, stopped);
                newRuntime.DataAvailable += onData;
                newRuntime.RecordingStopped += onStopped;

                AudioRuntimeOpenResult openResult;
                try
                {
                    openResult = await newRuntime.OpenAsync(resolution.Endpoint, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await CleanupFailedStartAsync(newRuntime, onData, onStopped).ConfigureAwait(false);
                    SetStatus(AudioCaptureState.Stopped, null);
                    throw;
                }
                catch (Exception ex)
                {
                    openResult = AudioRuntimeOpenResult.Failed(
                        AudioCaptureState.Faulted,
                        $"Audio runtime open failed: {ex.Message}");
                }

                if (!openResult.Success || openResult.InputFormat == null)
                {
                    await CleanupFailedStartAsync(newRuntime, onData, onStopped).ConfigureAwait(false);
                    var failedState = openResult.FailureState is AudioCaptureState.Unavailable
                        ? AudioCaptureState.Unavailable
                        : AudioCaptureState.Faulted;
                    var reason = openResult.FailureReason ?? "Audio runtime open failed.";
                    SetStatus(failedState, reason);
                    return AudioCaptureStartResult.Failed(failedState, reason);
                }

                StreamingAudioNormalizer newNormalizer;
                try
                {
                    newNormalizer = new StreamingAudioNormalizer(openResult.InputFormat);
                }
                catch (Exception ex)
                {
                    await CleanupFailedStartAsync(newRuntime, onData, onStopped).ConfigureAwait(false);
                    var reason = $"The selected endpoint format cannot be normalized: {ex.Message}";
                    SetStatus(AudioCaptureState.Unavailable, reason);
                    return AudioCaptureStartResult.Failed(AudioCaptureState.Unavailable, reason);
                }

                var newAssembler = new AudioFrameAssembler();
                newAssembler.StartSession(newSessionId);
                var newBuffer = new BoundedAudioFrameBuffer();
                BoundedAudioFrameBuffer oldBuffer;
                lock (stateLock)
                {
                    oldBuffer = frameBuffer;
                    runtime = newRuntime;
                    normalizer = newNormalizer;
                    assembler = newAssembler;
                    frameBuffer = newBuffer;
                    dataHandler = onData;
                    stoppedHandler = onStopped;
                    sessionId = newSessionId;
                    resolvedEndpoint = resolution.Endpoint;
                    inputFormat = openResult.InputFormat;
                    usedFallback = resolution.UsedFallback;
                    startDiagnostic = resolution.Diagnostic;
                    framesProduced = 0;
                    inputBytesReceived = 0;
                    normalizedBytesProduced = 0;
                    lastFrameSequence = 0;
                    startedAtUtc = DateTimeOffset.UtcNow;
                    lastFrameAtUtc = null;
                    failureReason = null;
                    acceptCallbacks = true;
                }
                oldBuffer.Complete();

                try
                {
                    await newRuntime.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await StopCoreAsync().ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    var reason = $"Unable to start WASAPI loopback capture: {ex.Message}";
                    var cleanupReason = await StopCoreAsync(reason).ConfigureAwait(false) ?? reason;
                    SetStatus(AudioCaptureState.Faulted, cleanupReason);
                    return AudioCaptureStartResult.Failed(AudioCaptureState.Faulted, cleanupReason);
                }

                SetStatus(AudioCaptureState.Running, null);
                return AudioCaptureStartResult.Started(
                    newSessionId,
                    resolution.Endpoint,
                    resolution.UsedFallback,
                    resolution.Diagnostic);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State == AudioCaptureState.Stopped && runtime == null)
                    return;
                SetStatus(AudioCaptureState.Stopping, FailureReason);
                var cleanupFailure = await StopCoreAsync().ConfigureAwait(false);
                SetStatus(AudioCaptureState.Stopped, cleanupFailure);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        private void OnDataAvailable(
            IAudioCaptureRuntime eventRuntime,
            Guid eventSessionId,
            AudioCaptureData data)
        {
            lock (callbackGate)
            {
                StreamingAudioNormalizer? currentNormalizer;
                AudioFrameAssembler? currentAssembler;
                BoundedAudioFrameBuffer currentBuffer;
                lock (stateLock)
                {
                    if (!acceptCallbacks || !ReferenceEquals(runtime, eventRuntime) || sessionId != eventSessionId)
                        return;
                    currentNormalizer = normalizer;
                    currentAssembler = assembler;
                    currentBuffer = frameBuffer;
                }

                try
                {
                    var normalized = currentNormalizer!.Process(data.Bytes.Span);
                    var frames = currentAssembler!.Append(normalized, EnsureUtc(data.CapturedAtUtc));
                    foreach (var frame in frames)
                    {
                        if (!currentBuffer.TryWrite(frame))
                            break;
                        lock (stateLock)
                        {
                            framesProduced++;
                            lastFrameSequence = frame.Sequence;
                            lastFrameAtUtc = frame.CapturedAtUtc;
                        }
                        InvokeSafely(FrameProduced, frame);
                    }

                    lock (stateLock)
                    {
                        inputBytesReceived += data.Bytes.Length;
                        normalizedBytesProduced += normalized.Length;
                    }
                }
                catch (Exception ex)
                {
                    lock (stateLock)
                        acceptCallbacks = false;
                    currentBuffer.Complete();
                    SetStatus(AudioCaptureState.Faulted, $"Audio normalization failed: {ex.Message}");
                }
            }
        }

        private void OnRuntimeStopped(
            IAudioCaptureRuntime eventRuntime,
            Guid eventSessionId,
            AudioRuntimeStopped stopped)
        {
            lock (stateLock)
            {
                if (!ReferenceEquals(runtime, eventRuntime) || sessionId != eventSessionId ||
                    stopped.Reason == AudioRuntimeStopReason.Expected)
                {
                    return;
                }
                acceptCallbacks = false;
                frameBuffer.Complete();
            }

            SetStatus(
                stopped.Reason == AudioRuntimeStopReason.Unavailable
                    ? AudioCaptureState.Unavailable
                    : AudioCaptureState.Faulted,
                stopped.FailureReason ?? "Audio capture stopped unexpectedly.");
        }

        private async Task<string?> StopCoreAsync(string? initialFailure = null)
        {
            IAudioCaptureRuntime? oldRuntime;
            EventHandler<AudioCaptureData>? oldDataHandler;
            EventHandler<AudioRuntimeStopped>? oldStoppedHandler;
            BoundedAudioFrameBuffer oldBuffer;
            lock (stateLock)
            {
                acceptCallbacks = false;
                oldRuntime = runtime;
                oldDataHandler = dataHandler;
                oldStoppedHandler = stoppedHandler;
                oldBuffer = frameBuffer;
            }

            lock (callbackGate) { }
            var failures = new List<string>();
            if (!string.IsNullOrWhiteSpace(initialFailure))
                failures.Add(initialFailure);

            if (oldRuntime != null)
            {
                try { await oldRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { failures.Add($"Runtime stop failed: {ex.Message}"); }

                if (oldDataHandler != null)
                    oldRuntime.DataAvailable -= oldDataHandler;
                if (oldStoppedHandler != null)
                    oldRuntime.RecordingStopped -= oldStoppedHandler;
                try { await oldRuntime.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { failures.Add($"Runtime disposal failed: {ex.Message}"); }
            }

            oldBuffer.Complete();
            lock (stateLock)
            {
                normalizer?.Reset();
                assembler?.Reset();
                runtime = null;
                normalizer = null;
                assembler = null;
                dataHandler = null;
                stoppedHandler = null;
                sessionId = null;
                inputFormat = null;
                acceptCallbacks = false;
            }

            return failures.Count == 0 ? null : string.Join(" ", failures);
        }

        private static async Task CleanupFailedStartAsync(
            IAudioCaptureRuntime failedRuntime,
            EventHandler<AudioCaptureData> onData,
            EventHandler<AudioRuntimeStopped> onStopped)
        {
            failedRuntime.DataAvailable -= onData;
            failedRuntime.RecordingStopped -= onStopped;
            try { await failedRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
            try { await failedRuntime.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        private void SetStatus(AudioCaptureState newState, string? reason)
        {
            AudioCaptureStatus status;
            lock (stateLock)
            {
                state = newState;
                failureReason = reason;
                status = new AudioCaptureStatus(newState, reason, DateTimeOffset.UtcNow);
            }
            InvokeSafely(StatusChanged, status);
        }

        private void InvokeSafely<T>(EventHandler<T>? handlers, T data)
        {
            if (handlers == null)
                return;
            foreach (EventHandler<T> handler in handlers.GetInvocationList())
            {
                try { handler(this, data); }
                catch { }
            }
        }

        private static DateTimeOffset EnsureUtc(DateTimeOffset timestamp) =>
            timestamp == default ? DateTimeOffset.UtcNow : timestamp.ToUniversalTime();

        private static string? NormalizeEndpointId(string? endpointId) =>
            string.IsNullOrWhiteSpace(endpointId) ? null : endpointId.Trim();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
                return;
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
                await endpointProvider.DisposeAsync().ConfigureAwait(false);
                frameBuffer.Dispose();
            }
            finally
            {
                lifecycleGate.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposeStarted) != 0)
                throw new ObjectDisposedException(nameof(AudioCaptureService));
        }
    }
}
