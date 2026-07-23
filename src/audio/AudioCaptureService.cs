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
        private readonly CallbackPublicationBarrier callbackBarrier = new();
        private readonly Action? beforeCallbackProcessing;
        private readonly AsyncLocal<TerminalCleanupOperation?> terminalStatusPublication = new();
        private readonly AsyncLocal<StatusPublicationContext?> currentStatusPublication = new();

        private AudioCaptureState state = AudioCaptureState.Stopped;
        private IAudioCaptureRuntime? runtime;
        private StreamingAudioNormalizer? normalizer;
        private AudioFrameAssembler? assembler;
        private BoundedAudioFrameBuffer frameBuffer = new();
        private EventHandler<AudioCaptureData>? dataHandler;
        private EventHandler<AudioRuntimeStopped>? stoppedHandler;
        private Guid? sessionId;
        private long callbackGeneration;
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
        private TerminalCleanupOperation? terminalCleanup;
        private int disposeStarted;

        public AudioCaptureService(
            IAudioEndpointProvider endpointProvider,
            IAudioCaptureRuntimeFactory runtimeFactory)
            : this(endpointProvider, runtimeFactory, null)
        {
        }

        internal AudioCaptureService(
            IAudioEndpointProvider endpointProvider,
            IAudioCaptureRuntimeFactory runtimeFactory,
            Action? beforeCallbackProcessing)
        {
            this.endpointProvider = endpointProvider ?? throw new ArgumentNullException(nameof(endpointProvider));
            this.runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
            this.beforeCallbackProcessing = beforeCallbackProcessing;
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
            var statuses = new List<AudioCaptureStatus>();
            AudioCaptureStartResult result;
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                result = await StartCoreAsync(savedEndpointId, statuses, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Release();
            }

            PublishStatuses(statuses);
            return result;
        }

        private async Task<AudioCaptureStartResult> StartCoreAsync(
            string? savedEndpointId,
            List<AudioCaptureStatus> statuses,
            CancellationToken cancellationToken)
        {
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

            statuses.Add(UpdateStatus(AudioCaptureState.Starting, null));
            requestedEndpointId = NormalizeEndpointId(savedEndpointId);
            AudioEndpointResolution resolution;
            try
            {
                resolution = await endpointProvider.ResolveAsync(requestedEndpointId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                statuses.Add(UpdateStatus(AudioCaptureState.Stopped, null));
                throw;
            }

            if (!resolution.Success || resolution.Endpoint == null)
            {
                var reason = resolution.FailureReason ?? "No active render endpoint is available.";
                statuses.Add(UpdateStatus(AudioCaptureState.Unavailable, reason));
                return AudioCaptureStartResult.Failed(AudioCaptureState.Unavailable, reason);
            }

            var newRuntime = runtimeFactory.Create();
            var newSessionId = Guid.NewGuid();
            var generation = callbackBarrier.BeginSession();
            EventHandler<AudioCaptureData> onData = (_, data) =>
                OnDataAvailable(newRuntime, newSessionId, generation, data);
            EventHandler<AudioRuntimeStopped> onStopped = (_, stopped) =>
                OnRuntimeStopped(newRuntime, newSessionId, generation, stopped);
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
                var cleanup = await CleanupFailedStartAsync(newRuntime, onData, onStopped)
                    .ConfigureAwait(false);
                statuses.Add(UpdateStatus(AudioCaptureState.Stopped, cleanup));
                throw;
            }
            catch (Exception ex)
            {
                openResult = AudioRuntimeOpenResult.Failed(
                    AudioCaptureState.Faulted,
                    $"Audio runtime open failed: {DescribeException(ex)}");
            }

            if (!openResult.Success || openResult.InputFormat == null)
            {
                var cleanup = await CleanupFailedStartAsync(newRuntime, onData, onStopped)
                    .ConfigureAwait(false);
                var failedState = openResult.FailureState is AudioCaptureState.Unavailable
                    ? AudioCaptureState.Unavailable
                    : AudioCaptureState.Faulted;
                var reason = CombineFailures(
                    openResult.FailureReason ?? "Audio runtime open failed.", cleanup)!;
                statuses.Add(UpdateStatus(failedState, reason));
                return AudioCaptureStartResult.Failed(failedState, reason);
            }

            StreamingAudioNormalizer newNormalizer;
            try
            {
                newNormalizer = new StreamingAudioNormalizer(openResult.InputFormat);
            }
            catch (Exception ex)
            {
                var cleanup = await CleanupFailedStartAsync(newRuntime, onData, onStopped)
                    .ConfigureAwait(false);
                var reason = CombineFailures(
                    $"The selected endpoint format cannot be normalized: {DescribeException(ex)}",
                    cleanup)!;
                statuses.Add(UpdateStatus(AudioCaptureState.Unavailable, reason));
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
                callbackGeneration = generation;
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
                terminalCleanup = null;
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
                var reason = $"Unable to start WASAPI loopback capture: {DescribeException(ex)}";
                var cleanupReason = await StopCoreAsync(reason).ConfigureAwait(false) ?? reason;
                statuses.Add(UpdateStatus(AudioCaptureState.Faulted, cleanupReason));
                return AudioCaptureStartResult.Failed(AudioCaptureState.Faulted, cleanupReason);
            }

            statuses.Add(UpdateStatus(AudioCaptureState.Running, null));
            return AudioCaptureStartResult.Started(
                newSessionId,
                resolution.Endpoint,
                resolution.UsedFallback,
                resolution.Diagnostic);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (currentStatusPublication.Value != null)
                currentStatusPublication.Value.StopRequested = true;
            var pendingTerminal = GetTerminalCleanup();
            if (pendingTerminal != null)
            {
                await WaitForTerminalCleanupAsync(pendingTerminal, cancellationToken).ConfigureAwait(false);
                return;
            }

            var statuses = new List<AudioCaptureStatus>();
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                pendingTerminal = GetTerminalCleanup();
                if (pendingTerminal == null)
                {
                    if (State != AudioCaptureState.Stopped || runtime != null)
                    {
                        statuses.Add(UpdateStatus(AudioCaptureState.Stopping, FailureReason));
                        var cleanupFailure = await StopCoreAsync().ConfigureAwait(false);
                        statuses.Add(UpdateStatus(AudioCaptureState.Stopped, cleanupFailure));
                    }
                }
            }
            finally
            {
                lifecycleGate.Release();
            }

            PublishStatuses(statuses);
            if (pendingTerminal != null)
                await WaitForTerminalCleanupAsync(pendingTerminal, cancellationToken).ConfigureAwait(false);
        }

        private void OnDataAvailable(
            IAudioCaptureRuntime eventRuntime,
            Guid eventSessionId,
            long generation,
            AudioCaptureData data)
        {
            var lease = callbackBarrier.TryEnter(generation);
            if (lease == null)
                return;

            try
            {
                IReadOnlyList<NormalizedAudioFrame> frames;
                List<NormalizedAudioFrame> acceptedFrames = [];
                BoundedAudioFrameBuffer currentBuffer;
                lock (callbackGate)
                {
                    beforeCallbackProcessing?.Invoke();
                    StreamingAudioNormalizer? currentNormalizer;
                    AudioFrameAssembler? currentAssembler;
                    lock (stateLock)
                    {
                        if (!acceptCallbacks || !ReferenceEquals(runtime, eventRuntime) ||
                            sessionId != eventSessionId || callbackGeneration != generation)
                        {
                            return;
                        }
                        currentNormalizer = normalizer;
                        currentAssembler = assembler;
                        currentBuffer = frameBuffer;
                    }

                    var normalized = currentNormalizer!.Process(data.Bytes.Span);
                    frames = currentAssembler!.Append(normalized, EnsureUtc(data.CapturedAtUtc));
                    foreach (var frame in frames)
                    {
                        if (!currentBuffer.TryWrite(frame))
                            break;
                        acceptedFrames.Add(frame);
                        lock (stateLock)
                        {
                            framesProduced++;
                            lastFrameSequence = frame.Sequence;
                            lastFrameAtUtc = frame.CapturedAtUtc;
                        }
                    }

                    lock (stateLock)
                    {
                        inputBytesReceived += data.Bytes.Length;
                        normalizedBytesProduced += normalized.Length;
                    }
                }

                foreach (var frame in acceptedFrames)
                {
                    if (!callbackBarrier.CanPublish(lease))
                        break;
                    PublishFrame(lease, frame);
                }
            }
            catch (Exception ex)
            {
                ScheduleTerminalCleanup(
                    eventRuntime,
                    eventSessionId,
                    generation,
                    AudioCaptureState.Faulted,
                    $"Audio normalization failed: {DescribeException(ex)}");
            }
            finally
            {
                callbackBarrier.Exit(lease);
            }
        }

        private void OnRuntimeStopped(
            IAudioCaptureRuntime eventRuntime,
            Guid eventSessionId,
            long generation,
            AudioRuntimeStopped stopped)
        {
            if (stopped.Reason == AudioRuntimeStopReason.Expected)
                return;

            ScheduleTerminalCleanup(
                eventRuntime,
                eventSessionId,
                generation,
                stopped.Reason == AudioRuntimeStopReason.Unavailable
                    ? AudioCaptureState.Unavailable
                    : AudioCaptureState.Faulted,
                stopped.FailureReason ?? "Audio capture stopped unexpectedly.");
        }

        private void ScheduleTerminalCleanup(
            IAudioCaptureRuntime eventRuntime,
            Guid eventSessionId,
            long generation,
            AudioCaptureState terminalState,
            string reason)
        {
            TerminalCleanupOperation operation;
            BoundedAudioFrameBuffer buffer;
            lock (stateLock)
            {
                if (!acceptCallbacks || !ReferenceEquals(runtime, eventRuntime) ||
                    sessionId != eventSessionId || callbackGeneration != generation ||
                    terminalCleanup != null)
                {
                    return;
                }

                acceptCallbacks = false;
                operation = new TerminalCleanupOperation();
                terminalCleanup = operation;
                buffer = frameBuffer;
            }

            callbackBarrier.Invalidate(generation);
            buffer.Complete();
            operation.Runner = RunTerminalCleanupAsync(operation, generation, terminalState, reason);
        }

        private async Task RunTerminalCleanupAsync(
            TerminalCleanupOperation operation,
            long generation,
            AudioCaptureState terminalState,
            string reason)
        {
            AudioCaptureStatus? status = null;
            try
            {
                await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var combined = await StopCoreAsync(reason, generation).ConfigureAwait(false) ?? reason;
                    status = UpdateStatus(terminalState, combined);
                }
                finally
                {
                    lifecycleGate.Release();
                }
            }
            catch (Exception ex)
            {
                status = UpdateStatus(
                    terminalState,
                    CombineFailures(reason, $"Terminal cleanup failed: {DescribeException(ex)}"));
            }
            finally
            {
                operation.CleanupCompleted.TrySetResult(null);
                try
                {
                    if (status != null)
                    {
                        var previous = terminalStatusPublication.Value;
                        terminalStatusPublication.Value = operation;
                        try { PublishStatus(status); }
                        finally { terminalStatusPublication.Value = previous; }
                    }
                }
                finally
                {
                    operation.Completion.TrySetResult(null);
                }
            }
        }

        private async Task<string?> StopCoreAsync(string? initialFailure = null, long? generation = null)
        {
            IAudioCaptureRuntime? oldRuntime;
            EventHandler<AudioCaptureData>? oldDataHandler;
            EventHandler<AudioRuntimeStopped>? oldStoppedHandler;
            BoundedAudioFrameBuffer oldBuffer;
            long oldGeneration;
            lock (stateLock)
            {
                acceptCallbacks = false;
                oldRuntime = runtime;
                oldDataHandler = dataHandler;
                oldStoppedHandler = stoppedHandler;
                oldBuffer = frameBuffer;
                oldGeneration = generation ?? callbackGeneration;
            }

            callbackBarrier.Invalidate(oldGeneration);
            await callbackBarrier.WaitForDrainAsync(oldGeneration).ConfigureAwait(false);
            var failures = new List<string>();
            AddFailure(failures, initialFailure);

            if (oldRuntime != null)
            {
                try { await oldRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { failures.Add($"Runtime stop failed: {DescribeException(ex)}"); }

                if (oldDataHandler != null)
                {
                    try { oldRuntime.DataAvailable -= oldDataHandler; }
                    catch (Exception ex) { failures.Add($"Data callback detach failed: {DescribeException(ex)}"); }
                }
                if (oldStoppedHandler != null)
                {
                    try { oldRuntime.RecordingStopped -= oldStoppedHandler; }
                    catch (Exception ex) { failures.Add($"Stopped callback detach failed: {DescribeException(ex)}"); }
                }
                try { await oldRuntime.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { failures.Add($"Runtime disposal failed: {DescribeException(ex)}"); }
            }

            oldBuffer.Complete();
            lock (callbackGate)
            {
                normalizer?.Reset();
                assembler?.Reset();
            }
            lock (stateLock)
            {
                if (ReferenceEquals(runtime, oldRuntime))
                {
                    runtime = null;
                    normalizer = null;
                    assembler = null;
                    dataHandler = null;
                    stoppedHandler = null;
                    sessionId = null;
                    inputFormat = null;
                    acceptCallbacks = false;
                }
            }

            return failures.Count == 0 ? null : string.Join(" ", failures);
        }

        private static async Task<string?> CleanupFailedStartAsync(
            IAudioCaptureRuntime failedRuntime,
            EventHandler<AudioCaptureData> onData,
            EventHandler<AudioRuntimeStopped> onStopped)
        {
            var failures = new List<string>();
            try { failedRuntime.DataAvailable -= onData; }
            catch (Exception ex) { failures.Add($"Data callback detach failed: {DescribeException(ex)}"); }
            try { failedRuntime.RecordingStopped -= onStopped; }
            catch (Exception ex) { failures.Add($"Stopped callback detach failed: {DescribeException(ex)}"); }
            try { await failedRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { failures.Add($"Runtime stop failed: {DescribeException(ex)}"); }
            try { await failedRuntime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { failures.Add($"Runtime disposal failed: {DescribeException(ex)}"); }
            return failures.Count == 0 ? null : string.Join(" ", failures);
        }

        private AudioCaptureStatus UpdateStatus(AudioCaptureState newState, string? reason)
        {
            lock (stateLock)
            {
                state = newState;
                failureReason = reason;
                return new AudioCaptureStatus(newState, reason, DateTimeOffset.UtcNow);
            }
        }

        private void PublishStatuses(IEnumerable<AudioCaptureStatus> statuses)
        {
            var first = true;
            foreach (var status in statuses)
            {
                if (!first && State != status.State)
                    return;
                first = false;
                if (PublishStatus(status))
                    return;
            }
        }

        private bool PublishStatus(AudioCaptureStatus status)
        {
            var handlers = StatusChanged;
            if (handlers == null)
                return false;
            var previous = currentStatusPublication.Value;
            var context = new StatusPublicationContext();
            currentStatusPublication.Value = context;
            try
            {
                foreach (EventHandler<AudioCaptureStatus> handler in handlers.GetInvocationList())
                {
                    try { handler(this, status); }
                    catch (Exception) { /* External subscribers are isolated by contract. */ }
                    if (context.StopRequested)
                        break;
                }
                return context.StopRequested;
            }
            finally
            {
                currentStatusPublication.Value = previous;
            }
        }

        private void PublishFrame(CallbackPublicationBarrier.Lease lease, NormalizedAudioFrame frame)
        {
            var handlers = FrameProduced;
            if (handlers == null)
                return;
            foreach (EventHandler<NormalizedAudioFrame> handler in handlers.GetInvocationList())
            {
                if (!callbackBarrier.CanPublish(lease))
                    return;
                using (callbackBarrier.EnterExternalPublication(lease))
                {
                    try { handler(this, frame); }
                    catch (Exception) { /* External subscribers are isolated by contract. */ }
                }
            }
        }

        private TerminalCleanupOperation? GetTerminalCleanup()
        {
            lock (stateLock)
                return terminalCleanup;
        }

        private Task WaitForTerminalCleanupAsync(
            TerminalCleanupOperation operation,
            CancellationToken cancellationToken)
        {
            var completion = ReferenceEquals(terminalStatusPublication.Value, operation)
                ? operation.CleanupCompleted.Task
                : operation.Completion.Task;
            return completion.WaitAsync(cancellationToken);
        }

        private static void AddFailure(List<string> failures, string? failure)
        {
            if (!string.IsNullOrWhiteSpace(failure))
                failures.Add(failure);
        }

        private static string? CombineFailures(params string?[] failures)
        {
            var retained = failures.Where(failure => !string.IsNullOrWhiteSpace(failure)).ToArray();
            return retained.Length == 0 ? null : string.Join(" ", retained!);
        }

        private static string DescribeException(Exception exception)
        {
            if (exception is AggregateException aggregate)
                return string.Join(" ", aggregate.Flatten().InnerExceptions.Select(DescribeException));
            return exception.Message;
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

        private sealed class TerminalCleanupOperation
        {
            internal TaskCompletionSource<object?> CleanupCompleted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<object?> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task Runner { get; set; } = Task.CompletedTask;
        }

        private sealed class StatusPublicationContext
        {
            internal bool StopRequested { get; set; }
        }

        private sealed class CallbackPublicationBarrier
        {
            private readonly object gate = new();
            private readonly HashSet<Lease> active = [];
            private readonly AsyncLocal<Lease?> externalPublication = new();
            private long generation;
            private bool accepting;

            internal long BeginSession()
            {
                lock (gate)
                {
                    generation++;
                    accepting = true;
                    return generation;
                }
            }

            internal Lease? TryEnter(long expectedGeneration)
            {
                lock (gate)
                {
                    if (!accepting || generation != expectedGeneration)
                        return null;
                    var lease = new Lease(expectedGeneration);
                    active.Add(lease);
                    return lease;
                }
            }

            internal bool CanPublish(Lease lease)
            {
                lock (gate)
                    return accepting && generation == lease.Generation && active.Contains(lease);
            }

            internal void Invalidate(long expectedGeneration)
            {
                lock (gate)
                {
                    if (generation == expectedGeneration)
                        accepting = false;
                }
            }

            internal Task WaitForDrainAsync(long expectedGeneration)
            {
                lock (gate)
                {
                    var excluded = externalPublication.Value;
                    var waits = active
                        .Where(lease => lease.Generation == expectedGeneration && !ReferenceEquals(lease, excluded))
                        .Select(lease => lease.Completion.Task)
                        .ToArray();
                    return waits.Length == 0 ? Task.CompletedTask : Task.WhenAll(waits);
                }
            }

            internal IDisposable EnterExternalPublication(Lease lease)
            {
                var previous = externalPublication.Value;
                externalPublication.Value = lease;
                return new Scope(() => externalPublication.Value = previous);
            }

            internal void Exit(Lease lease)
            {
                lock (gate)
                {
                    active.Remove(lease);
                    lease.Completion.TrySetResult(null);
                }
            }

            internal sealed class Lease(long generation)
            {
                internal long Generation { get; } = generation;
                internal TaskCompletionSource<object?> Completion { get; } =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            private sealed class Scope(Action dispose) : IDisposable
            {
                private Action? disposeAction = dispose;
                public void Dispose() => Interlocked.Exchange(ref disposeAction, null)?.Invoke();
            }
        }
    }
}
