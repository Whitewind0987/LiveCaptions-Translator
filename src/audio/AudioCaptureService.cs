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
        private readonly Action? beforeFrameDispatch;
        private readonly Action? beforeFrameBufferDispose;
        private readonly AsyncLocal<TerminalCleanupOperation?> terminalStatusPublication = new();
        private readonly AsyncLocal<StatusPublicationContext?> currentStatusPublication = new();

        private AudioCaptureState state = AudioCaptureState.Stopped;
        private IAudioCaptureRuntime? runtime;
        private StreamingAudioNormalizer? normalizer;
        private AudioFrameAssembler? assembler;
        private BoundedAudioFrameBuffer frameBuffer = new();
        private FramePublicationDispatcher? publicationDispatcher;
        private readonly List<FramePublicationDispatcher> retiredDispatchers = [];
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
            : this(endpointProvider, runtimeFactory, beforeCallbackProcessing, null, null)
        {
        }

        internal AudioCaptureService(
            IAudioEndpointProvider endpointProvider,
            IAudioCaptureRuntimeFactory runtimeFactory,
            Action? beforeCallbackProcessing,
            Action? beforeFrameDispatch,
            Action? beforeFrameBufferDispose)
        {
            this.endpointProvider = endpointProvider ?? throw new ArgumentNullException(nameof(endpointProvider));
            this.runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
            this.beforeCallbackProcessing = beforeCallbackProcessing;
            this.beforeFrameDispatch = beforeFrameDispatch;
            this.beforeFrameBufferDispose = beforeFrameBufferDispose;
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

        internal int FrameNotificationCapacity => FramePublicationDispatcher.DefaultCapacity;
        internal int FrameNotificationsQueued
        {
            get { lock (stateLock) return publicationDispatcher?.QueuedCount ?? 0; }
        }
        internal int MaximumFrameNotificationsQueued
        {
            get { lock (stateLock) return publicationDispatcher?.MaximumQueuedCount ?? 0; }
        }
        internal long FrameNotificationsDropped
        {
            get { lock (stateLock) return publicationDispatcher?.DroppedCount ?? 0; }
        }
        internal bool PublicationDispatchersCompleted
        {
            get
            {
                lock (stateLock)
                {
                    return publicationDispatcher == null &&
                        retiredDispatchers.All(dispatcher => dispatcher.Completion.IsCompleted);
                }
            }
        }

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
            FramePublicationDispatcher? dispatcherToActivate = null;
            if (result.Success)
            {
                lock (stateLock)
                {
                    if (state == AudioCaptureState.Running && sessionId == result.SessionId)
                        dispatcherToActivate = publicationDispatcher;
                }
            }
            dispatcherToActivate?.Activate();
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
            var newDispatcher = new FramePublicationDispatcher(
                newSessionId,
                generation,
                PublishFrame,
                ex => ScheduleTerminalCleanup(
                    newRuntime,
                    newSessionId,
                    generation,
                    AudioCaptureState.Faulted,
                    $"Frame notification dispatcher failed: {DescribeException(ex)}"),
                beforeFrameDispatch);
            BoundedAudioFrameBuffer oldBuffer;
            lock (stateLock)
            {
                oldBuffer = frameBuffer;
                runtime = newRuntime;
                normalizer = newNormalizer;
                assembler = newAssembler;
                frameBuffer = newBuffer;
                publicationDispatcher = newDispatcher;
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
                FramePublicationDispatcher currentDispatcher;
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
                        currentDispatcher = publicationDispatcher!;
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
                    currentDispatcher.TryEnqueue(new FramePublicationNotification(
                        eventSessionId,
                        generation,
                        frame));
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
            FramePublicationDispatcher? dispatcher;
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
                dispatcher = publicationDispatcher;
            }

            callbackBarrier.Invalidate(generation);
            dispatcher?.Invalidate();
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
            FramePublicationDispatcher? oldDispatcher;
            long oldGeneration;
            lock (stateLock)
            {
                acceptCallbacks = false;
                oldRuntime = runtime;
                oldDataHandler = dataHandler;
                oldStoppedHandler = stoppedHandler;
                oldBuffer = frameBuffer;
                oldDispatcher = publicationDispatcher;
                oldGeneration = generation ?? callbackGeneration;
            }

            callbackBarrier.Invalidate(oldGeneration);
            oldDispatcher?.Invalidate();
            await callbackBarrier.WaitForDrainAsync(oldGeneration).ConfigureAwait(false);
            var failures = new List<string>();
            AddFailure(failures, initialFailure);

            if (oldDispatcher != null)
            {
                try { AddFailure(failures, await oldDispatcher.StopAsync().ConfigureAwait(false)); }
                catch (Exception ex)
                {
                    failures.Add($"Frame notification dispatcher stop failed: {DescribeException(ex)}");
                }
            }

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
                    publicationDispatcher = null;
                    sessionId = null;
                    inputFormat = null;
                    acceptCallbacks = false;
                    if (oldDispatcher != null)
                        retiredDispatchers.Add(oldDispatcher);
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

        private void PublishFrame(
            FramePublicationDispatcher dispatcher,
            FramePublicationNotification notification)
        {
            var handlers = FrameProduced;
            if (handlers == null)
                return;
            foreach (EventHandler<NormalizedAudioFrame> handler in handlers.GetInvocationList())
            {
                if (!dispatcher.CanPublish(notification))
                    return;
                try { handler(this, notification.Frame); }
                catch (Exception) { /* External subscribers are isolated by contract. */ }
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
            var disposalFailures = new List<Exception>();
            try
            {
                try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    disposalFailures.Add(
                        new InvalidOperationException($"Service stop failed: {DescribeException(ex)}", ex));
                }

                try { await endpointProvider.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    disposalFailures.Add(new InvalidOperationException(
                        $"Endpoint provider disposal failed: {DescribeException(ex)}", ex));
                }

                FramePublicationDispatcher[] dispatchers;
                BoundedAudioFrameBuffer buffer;
                lock (stateLock)
                {
                    dispatchers = retiredDispatchers
                        .Append(publicationDispatcher)
                        .Where(dispatcher => dispatcher != null)
                        .Cast<FramePublicationDispatcher>()
                        .Distinct()
                        .ToArray();
                    buffer = frameBuffer;
                }
                foreach (var dispatcher in dispatchers)
                {
                    try { await dispatcher.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        disposalFailures.Add(
                            new InvalidOperationException(
                                $"Frame notification dispatcher disposal failed: {DescribeException(ex)}", ex));
                    }
                }

                try { buffer.Complete(); }
                catch (Exception ex)
                {
                    disposalFailures.Add(new InvalidOperationException(
                        $"Frame buffer completion failed: {DescribeException(ex)}", ex));
                }
                try { beforeFrameBufferDispose?.Invoke(); }
                catch (Exception ex)
                {
                    disposalFailures.Add(new InvalidOperationException(
                        $"Frame buffer disposal preparation failed: {DescribeException(ex)}", ex));
                }
                try { buffer.Dispose(); }
                catch (Exception ex)
                {
                    disposalFailures.Add(new InvalidOperationException(
                        $"Frame buffer disposal failed: {DescribeException(ex)}", ex));
                }
            }
            finally
            {
                try { lifecycleGate.Dispose(); }
                catch (Exception ex)
                {
                    disposalFailures.Add(new InvalidOperationException(
                        $"Lifecycle gate disposal failed: {DescribeException(ex)}", ex));
                }
            }

            if (disposalFailures.Count > 0)
            {
                lock (stateLock)
                {
                    failureReason = CombineFailures(
                        failureReason,
                        string.Join(" ", disposalFailures.Select(DescribeException)));
                }
                throw new AggregateException(disposalFailures);
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
                    var waits = active
                        .Where(lease => lease.Generation == expectedGeneration)
                        .Select(lease => lease.Completion.Task)
                        .ToArray();
                    return waits.Length == 0 ? Task.CompletedTask : Task.WhenAll(waits);
                }
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
        }
    }
}
