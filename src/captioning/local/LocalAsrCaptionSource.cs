using LiveCaptionsTranslator.worker;

namespace LiveCaptionsTranslator.captioning.local
{
    internal interface ILocalAsrPipeline : IAsyncDisposable
    {
        Guid? SessionId { get; }
        event EventHandler<CaptionEvent>? CaptionEventReceived;
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
    }

    internal sealed class AudioWorkerPipelineCaptionAdapter : ILocalAsrPipeline
    {
        private readonly AudioWorkerPipeline pipeline;
        private readonly string? endpointId;

        internal AudioWorkerPipelineCaptionAdapter(AudioWorkerPipeline pipeline, string? endpointId)
        {
            this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            this.endpointId = endpointId;
        }

        public Guid? SessionId => pipeline.Diagnostics.Capture.SessionId;

        public event EventHandler<CaptionEvent>? CaptionEventReceived
        {
            add => pipeline.CaptionEventReceived += value;
            remove => pipeline.CaptionEventReceived -= value;
        }

        public Task StartAsync(CancellationToken cancellationToken) =>
            pipeline.StartAsync(endpointId, cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) =>
            pipeline.StopAsync(cancellationToken);

        public ValueTask DisposeAsync() => pipeline.DisposeAsync();
    }

    public sealed class LocalAsrCaptionSource : ICaptionSource
    {
        private readonly Func<ILocalAsrPipeline> pipelineFactory;
        private readonly Action<Action> statusDispatcher;
        private readonly object stateLock = new();
        private readonly AsyncLocal<int> publicationDepth = new();
        private readonly AsyncLocal<int> statusPublicationDepth = new();
        private readonly Queue<VersionedStatus> statusQueue = [];

        private CaptionSourceState state = CaptionSourceState.Stopped;
        private string? failureReason;
        private ActiveRun? activeRun;
        private CancellationTokenSource? startCancellation;
        private Task<CaptionSourceStartResult>? startTask;
        private Task? stopTask;
        private long generation;
        private long statusVersion;
        private bool statusDispatchScheduled;
        private int disposeStarted;

        public LocalAsrCaptionSource(
            Func<AudioWorkerPipeline> pipelineFactory,
            string? endpointId = null)
            : this(() => new AudioWorkerPipelineCaptionAdapter(
                pipelineFactory?.Invoke() ?? throw new InvalidOperationException(
                    "The local ASR pipeline factory returned null."),
                endpointId))
        {
            ArgumentNullException.ThrowIfNull(pipelineFactory);
        }

        internal LocalAsrCaptionSource(
            Func<ILocalAsrPipeline> pipelineFactory,
            Action<Action>? statusDispatcher = null)
        {
            this.pipelineFactory = pipelineFactory ?? throw new ArgumentNullException(nameof(pipelineFactory));
            this.statusDispatcher = statusDispatcher ?? (action => action());
        }

        public string SourceId => "local-asr";

        public CaptionSourceState State
        {
            get { lock (stateLock) return state; }
        }

        public string? FailureReason
        {
            get { lock (stateLock) return failureReason; }
        }

        public event EventHandler<CaptionEvent>? CaptionEventReceived;
        public event EventHandler<CaptionSourceStatus>? StatusChanged;

        public Task<CaptionSourceStartResult> StartAsync(
            CancellationToken cancellationToken = default)
        {
            Task<CaptionSourceStartResult>? pendingStart;
            Task? pendingStop;
            TaskCompletionSource<CaptionSourceStartResult>? completion = null;
            CancellationTokenSource? ownedCancellation = null;
            var scheduleStatus = false;
            long runGeneration = 0;

            lock (stateLock)
            {
                ThrowIfDisposed();

                if (state == CaptionSourceState.Running && activeRun?.SessionId is Guid sessionId)
                    return Task.FromResult(CaptionSourceStartResult.Started(sessionId));

                pendingStart = startTask;
                pendingStop = stopTask;
                if (pendingStart == null && pendingStop == null)
                {
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    ownedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    runGeneration = ++generation;
                    startTask = completion.Task;
                    startCancellation = ownedCancellation;
                    scheduleStatus = ChangeStateLocked(CaptionSourceState.Starting, null);
                }
            }

            if (pendingStart != null)
                return pendingStart.WaitAsync(cancellationToken);
            if (pendingStop != null)
                return StartAfterStopAsync(pendingStop, cancellationToken);

            ScheduleStatusDrain(scheduleStatus);
            _ = RunStartAsync(runGeneration, ownedCancellation!, completion!);
            return completion!.Task;
        }

        private async Task<CaptionSourceStartResult> StartAfterStopAsync(
            Task pendingStop,
            CancellationToken cancellationToken)
        {
            await pendingStop.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await StartAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task RunStartAsync(
            long runGeneration,
            CancellationTokenSource ownedCancellation,
            TaskCompletionSource<CaptionSourceStartResult> completion)
        {
            ActiveRun? run = null;
            try
            {
                ownedCancellation.Token.ThrowIfCancellationRequested();
                var pipeline = pipelineFactory() ??
                    throw new InvalidOperationException("The local ASR pipeline factory returned null.");
                run = new ActiveRun(runGeneration, pipeline);
                run.Handler = (_, captionEvent) => OnPipelineCaptionEvent(run, captionEvent);
                pipeline.CaptionEventReceived += run.Handler;

                lock (stateLock)
                {
                    if (runGeneration != generation || stopTask != null ||
                        Volatile.Read(ref disposeStarted) != 0)
                    {
                        throw new OperationCanceledException(ownedCancellation.Token);
                    }

                    activeRun = run;
                }

                await pipeline.StartAsync(ownedCancellation.Token).ConfigureAwait(false);

                var sessionId = pipeline.SessionId;
                if (!sessionId.HasValue || sessionId.Value == Guid.Empty)
                    throw new InvalidOperationException("The local ASR pipeline did not establish a capture session.");

                bool scheduleRunningStatus;
                lock (stateLock)
                {
                    if (runGeneration != generation || stopTask != null ||
                        ownedCancellation.IsCancellationRequested ||
                        Volatile.Read(ref disposeStarted) != 0)
                    {
                        throw new OperationCanceledException(ownedCancellation.Token);
                    }

                    if (run.Gate.ActiveSessionId.HasValue &&
                        run.Gate.ActiveSessionId.Value != sessionId.Value)
                    {
                        throw new InvalidOperationException(
                            "The local ASR pipeline capture session does not match its caption session.");
                    }

                    run.SessionId = sessionId.Value;
                    scheduleRunningStatus = ChangeStateLocked(CaptionSourceState.Running, null);
                }

                ScheduleStatusDrain(scheduleRunningStatus);
                lock (stateLock)
                {
                    if (runGeneration != generation || stopTask != null ||
                        ownedCancellation.IsCancellationRequested ||
                        Volatile.Read(ref disposeStarted) != 0)
                    {
                        throw new OperationCanceledException(ownedCancellation.Token);
                    }

                    startTask = null;
                    startCancellation = null;
                    completion.TrySetResult(CaptionSourceStartResult.Started(sessionId.Value));
                }
                ownedCancellation.Dispose();
            }
            catch (OperationCanceledException) when (ownedCancellation.IsCancellationRequested)
            {
                var canceledToken = ownedCancellation.Token;
                var cleanupFailure = run == null ? null : await CleanupRunAsync(run).ConfigureAwait(false);
                var scheduleStoppedStatus = false;
                lock (stateLock)
                {
                    if (ReferenceEquals(activeRun, run)) activeRun = null;
                    startTask = null;
                    startCancellation = null;
                    if (stopTask == null && Volatile.Read(ref disposeStarted) == 0)
                        scheduleStoppedStatus = ChangeStateLocked(
                            CaptionSourceState.Stopped, cleanupFailure);
                }

                ownedCancellation.Dispose();
                ScheduleStatusDrain(scheduleStoppedStatus);
                completion.TrySetCanceled(canceledToken);
            }
            catch (Exception ex)
            {
                var cleanupFailure = run == null ? null : await CleanupRunAsync(run).ConfigureAwait(false);
                var reason = CombineFailure($"Local ASR pipeline failed to start: {ex.Message}", cleanupFailure);
                var scheduleFailedStatus = false;
                lock (stateLock)
                {
                    if (ReferenceEquals(activeRun, run)) activeRun = null;
                    startTask = null;
                    startCancellation = null;
                    if (stopTask == null && Volatile.Read(ref disposeStarted) == 0)
                        scheduleFailedStatus = ChangeStateLocked(CaptionSourceState.Faulted, reason);
                }

                ownedCancellation.Dispose();
                ScheduleStatusDrain(scheduleFailedStatus);
                completion.TrySetResult(CaptionSourceStartResult.Failed(
                    CaptionSourceState.Faulted, reason));
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Task? existingStop;
            TaskCompletionSource? completion = null;
            CancellationTokenSource? cancellation = null;
            var scheduleStatus = false;

            lock (stateLock)
            {
                existingStop = stopTask;
                if (activeRun != null && publicationDepth.Value != 0)
                    activeRun.PublicationEpoch++;
                if (existingStop == null)
                {
                    if (activeRun == null && startTask == null)
                        return Task.CompletedTask;

                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    stopTask = completion.Task;
                    existingStop = completion.Task;
                    cancellation = startCancellation;
                    scheduleStatus = ChangeStateLocked(CaptionSourceState.Stopping, failureReason);
                }
            }

            if (completion != null)
            {
                try { cancellation?.Cancel(); }
                catch (ObjectDisposedException) { }
                ScheduleStatusDrain(scheduleStatus);
                _ = RunStopAsync(completion);
            }

            return publicationDepth.Value != 0 || statusPublicationDepth.Value != 0
                ? Task.CompletedTask
                : existingStop!.WaitAsync(cancellationToken);
        }

        private async Task RunStopAsync(TaskCompletionSource completion)
        {
            Task<CaptionSourceStartResult>? pendingStart;
            lock (stateLock) pendingStart = startTask;
            if (pendingStart != null)
            {
                try { await pendingStart.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            ActiveRun? run;
            lock (stateLock) run = activeRun;
            var cleanupFailure = run == null ? null : await CleanupRunAsync(run).ConfigureAwait(false);

            bool scheduleFinalStatus;
            lock (stateLock)
            {
                if (ReferenceEquals(activeRun, run)) activeRun = null;
                stopTask = null;
                scheduleFinalStatus = ChangeStateLocked(
                    cleanupFailure == null ? CaptionSourceState.Stopped : CaptionSourceState.Faulted,
                    cleanupFailure);
            }

            ScheduleStatusDrain(scheduleFinalStatus);
            if (cleanupFailure == null) completion.TrySetResult();
            else completion.TrySetException(new InvalidOperationException(cleanupFailure));
        }

        private async Task<string?> CleanupRunAsync(ActiveRun run)
        {
            Task cleanup;
            lock (run)
                cleanup = run.CleanupTask ??= CleanupRunCoreAsync(run);

            try
            {
                await cleanup.ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private async Task CleanupRunCoreAsync(ActiveRun run)
        {
            var failures = new List<Exception>();
            try { await run.Pipeline.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(new InvalidOperationException("Pipeline stop failed.", ex)); }

            EventHandler<CaptionEvent>? handler;
            Task? publications = null;
            lock (stateLock)
            {
                run.AcceptEvents = false;
                handler = run.Handler;
                run.Handler = null;
                if (run.PublicationCount != 0 && publicationDepth.Value == 0)
                    publications = run.PublicationsDrained.Task;
            }

            if (handler != null)
                run.Pipeline.CaptionEventReceived -= handler;
            if (publications != null)
                await publications.ConfigureAwait(false);

            try { await run.Pipeline.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(new InvalidOperationException("Pipeline disposal failed.", ex)); }

            if (failures.Count != 0)
                throw new AggregateException("Local ASR pipeline cleanup failed.", failures);
        }

        private void OnPipelineCaptionEvent(ActiveRun run, CaptionEvent captionEvent)
        {
            var publish = false;
            long publicationEpoch = 0;
            lock (stateLock)
            {
                if (!ReferenceEquals(activeRun, run) || !run.AcceptEvents ||
                    run.Generation != generation)
                {
                    return;
                }

                if (run.SessionId.HasValue && captionEvent.SessionId != run.SessionId.Value)
                    return;

                if (!run.Gate.TryAccept(captionEvent, out _))
                    return;

                if (captionEvent.Kind == CaptionEventKind.Reset)
                {
                    run.PublishedStableCaption = null;
                    publish = true;
                }
                else if (captionEvent.Kind == CaptionEventKind.Partial)
                {
                    return;
                }
                else
                {
                    var identity = new StableCaptionIdentity(
                        captionEvent.SessionId, captionEvent.SegmentId, captionEvent.Revision);
                    if (run.PublishedStableCaption == identity)
                        return;

                    run.PublishedStableCaption = identity;
                    if (captionEvent.Kind == CaptionEventKind.Committed)
                        captionEvent = AsFinal(captionEvent);
                    publish = true;
                }

                if (run.PublicationCount == 0)
                {
                    run.PublicationsDrained = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }
                run.PublicationCount++;
                publicationEpoch = run.PublicationEpoch;
            }

            if (publish)
                PublishCaption(run, captionEvent, publicationEpoch);
        }

        private void PublishCaption(
            ActiveRun run,
            CaptionEvent captionEvent,
            long publicationEpoch)
        {
            publicationDepth.Value++;
            try
            {
                foreach (EventHandler<CaptionEvent> handler in
                         CaptionEventReceived?.GetInvocationList() ?? [])
                {
                    lock (stateLock)
                    {
                        if (!ReferenceEquals(activeRun, run) ||
                            run.Generation != generation ||
                            run.PublicationEpoch != publicationEpoch)
                        {
                            return;
                        }
                    }

                    try { handler(this, captionEvent); }
                    catch
                    {
                        // One subscriber must not corrupt source lifecycle or block cleanup.
                    }
                }
            }
            finally
            {
                publicationDepth.Value--;
                lock (stateLock)
                {
                    run.PublicationCount--;
                    if (run.PublicationCount == 0)
                        run.PublicationsDrained.TrySetResult();
                }
            }
        }

        private bool ChangeStateLocked(
            CaptionSourceState newState,
            string? reason)
        {
            state = newState;
            failureReason = reason;
            var status = new CaptionSourceStatus(
                SourceId, newState, reason, DateTimeOffset.UtcNow);
            statusQueue.Enqueue(new VersionedStatus(++statusVersion, status));
            if (statusDispatchScheduled)
                return false;

            statusDispatchScheduled = true;
            return true;
        }

        private void ScheduleStatusDrain(bool schedule)
        {
            if (schedule)
                statusDispatcher(DrainStatusQueue);
        }

        private void DrainStatusQueue()
        {
            while (true)
            {
                VersionedStatus publication;
                lock (stateLock)
                {
                    if (statusQueue.Count == 0)
                    {
                        statusDispatchScheduled = false;
                        return;
                    }

                    publication = statusQueue.Dequeue();
                    if (publication.Version != statusVersion)
                        continue;
                }

                statusPublicationDepth.Value++;
                try
                {
                    foreach (EventHandler<CaptionSourceStatus> handler in
                             StatusChanged?.GetInvocationList() ?? [])
                    {
                        lock (stateLock)
                        {
                            if (publication.Version != statusVersion)
                                break;
                        }

                        try { handler(this, publication.Status); }
                        catch
                        {
                            // One subscriber must not corrupt source lifecycle or block cleanup.
                        }
                    }
                }
                finally
                {
                    statusPublicationDepth.Value--;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
                return;

            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposeStarted) != 0)
                throw new ObjectDisposedException(nameof(LocalAsrCaptionSource));
        }

        private static string CombineFailure(string original, string? cleanup) =>
            string.IsNullOrWhiteSpace(cleanup) ? original : $"{original} {cleanup}";

        private static CaptionEvent AsFinal(CaptionEvent captionEvent) =>
            new(
                captionEvent.SchemaVersion,
                captionEvent.SessionId,
                captionEvent.Sequence,
                captionEvent.SegmentId,
                captionEvent.Revision,
                CaptionEventKind.Final,
                captionEvent.Text,
                captionEvent.AudioStartMilliseconds,
                captionEvent.AudioEndMilliseconds,
                captionEvent.EmittedAtUtc);

        private sealed class ActiveRun
        {
            internal ActiveRun(long generation, ILocalAsrPipeline pipeline)
            {
                Generation = generation;
                Pipeline = pipeline;
            }

            internal long Generation { get; }
            internal ILocalAsrPipeline Pipeline { get; }
            internal CaptionEventGate Gate { get; } = new();
            internal StableCaptionIdentity? PublishedStableCaption { get; set; }
            internal TaskCompletionSource PublicationsDrained { get; set; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Guid? SessionId { get; set; }
            internal EventHandler<CaptionEvent>? Handler { get; set; }
            internal int PublicationCount { get; set; }
            internal bool AcceptEvents { get; set; } = true;
            internal long PublicationEpoch { get; set; }
            internal Task? CleanupTask { get; set; }
        }

        private readonly record struct VersionedStatus(
            long Version,
            CaptionSourceStatus Status);

        private readonly record struct StableCaptionIdentity(
            Guid SessionId,
            long SegmentId,
            long Revision);
    }
}
