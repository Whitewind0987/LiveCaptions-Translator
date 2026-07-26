using LiveCaptionsTranslator.captioning.windows;

namespace LiveCaptionsTranslator.captioning
{
    public enum CaptionSourceKind
    {
        WindowsLiveCaptions,
        LocalAsr
    }

    public sealed class CaptionSourceCoordinator : IAsyncDisposable
    {
        // Cancellation registrations restore their captured ExecutionContext, so AsyncLocal
        // alone cannot identify a synchronous lifecycle call made from CancellationTokenSource.Cancel.
        [ThreadStatic]
        private static int synchronousCancellationDepth;

        public const CaptionSourceKind DefaultSource = CaptionSourceKind.WindowsLiveCaptions;

        private const string CoordinatorSourceId = "caption-source-coordinator";

        private readonly Func<ICaptionSource> windowsLiveCaptionsFactory;
        private readonly Func<ICaptionSource>? localAsrFactory;
        private readonly Func<ICaptionSource, CaptionSourceHost> hostFactory;
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);
        private readonly object stateLock = new();
        private readonly AsyncLocal<int> callbackDepth = new();

        private OwnedHost? currentOwner;
        private CancellationTokenSource? selectionCancellation;
        private Task? stopTask;
        private Task? disposeTask;
        private CaptionSourceState state = CaptionSourceState.Stopped;
        private string? failureReason;
        private string? selectionCleanupFailure;
        private AcceptedCaptionSnapshot? latestSnapshot;
        private long sessionGeneration;
        private long notificationVersion;
        private bool disposeRequested;

        public CaptionSourceCoordinator(
            Func<ICaptionSource> windowsLiveCaptionsFactory,
            Func<ICaptionSource>? localAsrFactory = null)
            : this(
                windowsLiveCaptionsFactory,
                localAsrFactory,
                source => new CaptionSourceHost(source))
        {
        }

        internal CaptionSourceCoordinator(
            Func<ICaptionSource> windowsLiveCaptionsFactory,
            Func<ICaptionSource>? localAsrFactory,
            Func<ICaptionSource, CaptionSourceHost> hostFactory)
        {
            this.windowsLiveCaptionsFactory = windowsLiveCaptionsFactory ??
                throw new ArgumentNullException(nameof(windowsLiveCaptionsFactory));
            this.localAsrFactory = localAsrFactory;
            this.hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        }

        public CaptionSourceKind? CurrentSource
        {
            get { lock (stateLock) return currentOwner?.Kind; }
        }

        public CaptionSourceState State
        {
            get { lock (stateLock) return state; }
        }

        public string? FailureReason
        {
            get { lock (stateLock) return failureReason; }
        }

        public INativeCaptionWindowControl? NativeWindowControl
        {
            get { lock (stateLock) return currentOwner?.Host.NativeWindowControl; }
        }

        public event EventHandler<CaptionSourceStatus>? StatusChanged;
        public event EventHandler<AcceptedCaptionSnapshot?>? SnapshotChanged;

        public CaptionSourceLatestState ReadLatestState()
        {
            lock (stateLock)
                return new CaptionSourceLatestState(
                    state,
                    sessionGeneration,
                    state == CaptionSourceState.Running ? latestSnapshot : null);
        }

        public Task<CaptionSourceStartResult> StartAsync(
            CancellationToken cancellationToken = default) =>
            SelectAsync(DefaultSource, cancellationToken);

        public async Task<CaptionSourceStartResult> SelectAsync(
            CaptionSourceKind kind,
            CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(kind))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Caption source is not supported.");

            while (true)
            {
                Task? pendingStop;
                lock (stateLock)
                {
                    ThrowIfDisposeRequested();
                    pendingStop = stopTask;
                }

                if (pendingStop != null)
                {
                    await pendingStop.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    lock (stateLock)
                    {
                        ThrowIfDisposeRequested();
                        pendingStop = stopTask;
                        if (pendingStop == null &&
                            currentOwner is { ForwardingEnabled: true } owner &&
                            owner.Kind == kind)
                        {
                            return owner.StartResult!;
                        }
                    }

                    if (pendingStop == null)
                        return await ReplaceSourceAsync(kind, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lifecycleGate.Release();
                }

                await pendingStop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<CaptionSourceStartResult> ReplaceSourceAsync(
            CaptionSourceKind kind,
            CancellationToken cancellationToken)
        {
            CancellationTokenSource ownedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            OwnedHost? previous;
            Notification invalidation;
            lock (stateLock)
            {
                previous = DetachCurrentOwnerLocked(out invalidation);
                selectionCancellation = ownedCancellation;
            }

            Publish(invalidation);
            try
            {
                if (previous != null)
                {
                    var cleanupFailure = await CleanupOwnerAsync(previous).ConfigureAwait(false);
                    if (cleanupFailure != null)
                    {
                        return RecordFailure(
                            CaptionSourceState.Faulted,
                            $"The previous caption source could not be cleaned up: {cleanupFailure}");
                    }
                }

                if (ownedCancellation.IsCancellationRequested)
                {
                    bool shutdownOwnsTerminalState;
                    lock (stateLock)
                        shutdownOwnsTerminalState = stopTask != null || disposeTask != null;
                    if (!shutdownOwnsTerminalState)
                        PublishTerminalState(null);
                    ownedCancellation.Token.ThrowIfCancellationRequested();
                }
                var factory = GetFactory(kind);
                if (factory == null)
                {
                    return RecordFailure(
                        CaptionSourceState.Unavailable,
                        $"No production factory is configured for caption source '{kind}'.");
                }

                ICaptionSource? source = null;
                CaptionSourceHost? host = null;
                OwnedHost? target = null;
                var targetPublished = false;
                try
                {
                    source = factory() ?? throw new InvalidOperationException(
                        $"The caption-source factory for '{kind}' returned null.");
                    ownedCancellation.Token.ThrowIfCancellationRequested();
                    host = hostFactory(source) ?? throw new InvalidOperationException(
                        "The caption-source host factory returned null.");
                    target = CreateOwner(kind, source.SourceId, host);
                    source = null;
                    host = null;

                    lock (stateLock)
                    {
                        ownedCancellation.Token.ThrowIfCancellationRequested();
                        ThrowIfDisposeRequested();
                        currentOwner = target;
                        targetPublished = true;
                        state = target.Host.State;
                        failureReason = target.Host.FailureReason;
                        latestSnapshot = null;
                    }

                    var result = await target.Host.StartAsync(ownedCancellation.Token)
                        .ConfigureAwait(false);
                    ownedCancellation.Token.ThrowIfCancellationRequested();

                    if (!result.Success)
                    {
                        Notification failedInvalidation = default;
                        var ownsCleanup = !targetPublished ||
                            TryInvalidateOwner(target, out failedInvalidation);
                        Publish(failedInvalidation);
                        if (!ownsCleanup)
                        {
                            throw new OperationCanceledException(
                                "Caption-source selection was superseded by shutdown.",
                                ownedCancellation.Token);
                        }
                        var cleanupFailure = await CleanupOwnerAsync(target).ConfigureAwait(false);
                        var reason = CombineFailure(result.FailureReason!, cleanupFailure);
                        return RecordFailure(result.State, reason);
                    }

                    lock (stateLock)
                    {
                        if (!IsCurrentOwnerLocked(target))
                            throw new OperationCanceledException(ownedCancellation.Token);

                        target.StartResult = result;
                    }

                    return result;
                }
                catch (OperationCanceledException)
                {
                    string? cleanupFailure;
                    var publishTerminalState = true;
                    if (target != null)
                    {
                        Notification canceledInvalidation = default;
                        var ownsCleanup = !targetPublished ||
                            TryInvalidateOwner(target, out canceledInvalidation);
                        Publish(canceledInvalidation);
                        cleanupFailure = ownsCleanup
                            ? await CleanupOwnerAsync(target).ConfigureAwait(false)
                            : null;
                        lock (stateLock)
                        {
                            publishTerminalState = ownsCleanup &&
                                stopTask == null && disposeTask == null;
                        }
                    }
                    else if (host != null)
                    {
                        cleanupFailure = await CleanupHostAsync(host).ConfigureAwait(false);
                    }
                    else if (source != null)
                    {
                        cleanupFailure = await CleanupSourceAsync(source).ConfigureAwait(false);
                    }
                    else
                    {
                        cleanupFailure = null;
                    }

                    if (publishTerminalState)
                        PublishTerminalState(cleanupFailure);
                    else if (cleanupFailure != null)
                        RecordSelectionCleanupFailure(cleanupFailure);
                    if (cleanupFailure != null)
                    {
                        throw new InvalidOperationException(
                            $"Caption-source cancellation cleanup failed: {cleanupFailure}");
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    string? cleanupFailure;
                    if (target != null)
                    {
                        Notification failedInvalidation = default;
                        var ownsCleanup = !targetPublished ||
                            TryInvalidateOwner(target, out failedInvalidation);
                        Publish(failedInvalidation);
                        if (!ownsCleanup)
                        {
                            throw new OperationCanceledException(
                                "Caption-source selection was superseded by shutdown.",
                                ex,
                                ownedCancellation.Token);
                        }
                        cleanupFailure = await CleanupOwnerAsync(target).ConfigureAwait(false);
                    }
                    else if (host != null)
                    {
                        cleanupFailure = await CleanupHostAsync(host).ConfigureAwait(false);
                    }
                    else if (source != null)
                    {
                        cleanupFailure = await CleanupSourceAsync(source).ConfigureAwait(false);
                    }
                    else
                    {
                        cleanupFailure = null;
                    }

                    bool shutdownOwnsTerminalState;
                    lock (stateLock)
                        shutdownOwnsTerminalState = stopTask != null || disposeTask != null;
                    if (shutdownOwnsTerminalState)
                    {
                        if (cleanupFailure != null)
                            RecordSelectionCleanupFailure(cleanupFailure);
                        throw new OperationCanceledException(
                            "Caption-source selection was superseded by shutdown.",
                            ex,
                            ownedCancellation.Token);
                    }

                    return RecordFailure(
                        CaptionSourceState.Faulted,
                        CombineFailure($"Caption source '{kind}' failed to start: {ex.Message}", cleanupFailure));
                }
            }
            finally
            {
                lock (stateLock)
                {
                    if (ReferenceEquals(selectionCancellation, ownedCancellation))
                        selectionCancellation = null;
                }
                ownedCancellation.Dispose();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            // A forwarded callback cannot synchronously join cleanup because the source may
            // still own the lifecycle gate. It starts the tracked operation and returns; a
            // later external StopAsync/DisposeAsync call joins that same operation.
            var reentrant = IsReentrantLifecycleCall;
            Task operation;
            TaskCompletionSource? completion = null;
            OwnedHost? owner = null;
            Notification invalidation = default;
            CancellationTokenSource? cancellationToCancel = null;

            lock (stateLock)
            {
                if (disposeTask != null)
                    operation = disposeTask;
                else if (stopTask != null)
                    operation = stopTask;
                else if (currentOwner == null && selectionCancellation == null)
                    return Task.CompletedTask;
                else
                {
                    cancellationToCancel = selectionCancellation;
                    selectionCancellation = null;
                    owner = DetachCurrentOwnerLocked(out invalidation);
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    stopTask = completion.Task;
                    operation = completion.Task;
                }
            }

            if (completion != null)
            {
                var cancellationFailure = CancelSelectionOutsideLock(cancellationToCancel);
                Publish(invalidation);
                _ = RunStopAsync(owner, cancellationFailure, completion);
            }

            return reentrant ? Task.CompletedTask : operation.WaitAsync(cancellationToken);
        }

        private async Task RunStopAsync(
            OwnedHost? owner,
            string? cancellationFailure,
            TaskCompletionSource completion)
        {
            try
            {
                await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var pendingSelectionFailure = TakeSelectionCleanupFailure();
                    var ownerCleanupFailure = owner == null ? null :
                        await CleanupOwnerAsync(owner).ConfigureAwait(false);
                    var cleanupFailure = CombineFailures(
                        cancellationFailure,
                        pendingSelectionFailure,
                        ownerCleanupFailure);
                    PublishTerminalState(cleanupFailure);

                    if (cleanupFailure == null)
                        completion.TrySetResult();
                    else
                        completion.TrySetException(new InvalidOperationException(cleanupFailure));
                }
                finally
                {
                    lifecycleGate.Release();
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                lock (stateLock)
                {
                    if (ReferenceEquals(stopTask, completion.Task))
                        stopTask = null;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            var reentrant = IsReentrantLifecycleCall;
            Task operation;
            TaskCompletionSource? completion = null;
            OwnedHost? owner = null;
            Notification invalidation = default;
            Task? pendingStop = null;
            CancellationTokenSource? cancellationToCancel = null;

            lock (stateLock)
            {
                if (disposeTask != null)
                {
                    operation = disposeTask;
                }
                else
                {
                    disposeRequested = true;
                    cancellationToCancel = selectionCancellation;
                    selectionCancellation = null;
                    owner = DetachCurrentOwnerLocked(out invalidation);
                    pendingStop = stopTask;
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    disposeTask = completion.Task;
                    operation = completion.Task;
                }
            }

            if (completion != null)
            {
                var cancellationFailure = CancelSelectionOutsideLock(cancellationToCancel);
                Publish(invalidation);
                _ = RunDisposeAsync(
                    owner,
                    pendingStop,
                    cancellationFailure,
                    completion);
            }

            return reentrant ? ValueTask.CompletedTask : new ValueTask(operation);
        }

        private async Task RunDisposeAsync(
            OwnedHost? owner,
            Task? pendingStop,
            string? cancellationFailure,
            TaskCompletionSource completion)
        {
            try
            {
                string? pendingStopFailure = null;
                if (pendingStop != null)
                {
                    try
                    {
                        await pendingStop.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        pendingStopFailure = FormatFailure(ex);
                    }
                }

                await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var pendingSelectionFailure = TakeSelectionCleanupFailure();
                    var ownerCleanupFailure = owner == null ? null :
                        await CleanupOwnerAsync(owner).ConfigureAwait(false);
                    var cleanupFailure = CombineFailures(
                        pendingStopFailure,
                        cancellationFailure,
                        pendingSelectionFailure,
                        ownerCleanupFailure);
                    PublishTerminalState(cleanupFailure);
                    if (cleanupFailure == null)
                        completion.TrySetResult();
                    else
                        completion.TrySetException(new InvalidOperationException(cleanupFailure));
                }
                finally
                {
                    lifecycleGate.Release();
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        private OwnedHost CreateOwner(
            CaptionSourceKind kind,
            string sourceId,
            CaptionSourceHost host)
        {
            OwnedHost owner;
            lock (stateLock)
                owner = new OwnedHost(kind, sourceId, host);

            owner.StatusHandler = (_, status) => OnHostStatusChanged(owner, status);
            owner.SnapshotHandler = (_, snapshot) => OnHostSnapshotChanged(owner, snapshot);
            host.StatusChanged += owner.StatusHandler;
            host.SnapshotChanged += owner.SnapshotHandler;
            return owner;
        }

        private void OnHostStatusChanged(OwnedHost owner, CaptionSourceStatus status)
        {
            Notification notification;
            lock (stateLock)
            {
                if (!IsCurrentOwnerLocked(owner))
                    return;

                state = status.State;
                failureReason = status.FailureReason;
                if (status.State != CaptionSourceState.Running)
                    latestSnapshot = null;
                notification = CreateNotificationLocked(owner, status);
            }

            Publish(notification);
        }

        private void OnHostSnapshotChanged(OwnedHost owner, AcceptedCaptionSnapshot? snapshot)
        {
            Notification notification;
            AcceptedCaptionSnapshot? forwardedSnapshot;
            lock (stateLock)
            {
                if (!IsCurrentOwnerLocked(owner))
                    return;

                var hostGeneration = owner.Host.SessionGeneration;
                if (hostGeneration > owner.LastHostSessionGeneration)
                {
                    sessionGeneration += hostGeneration - owner.LastHostSessionGeneration;
                    owner.LastHostSessionGeneration = hostGeneration;
                }

                forwardedSnapshot = snapshot == null ? null : new AcceptedCaptionSnapshot(
                    snapshot.SessionId,
                    snapshot.Sequence,
                    snapshot.SegmentId,
                    snapshot.Revision,
                    snapshot.Text,
                    sessionGeneration);
                latestSnapshot = forwardedSnapshot;
                notification = CreateNotificationLocked(owner, forwardedSnapshot);
            }

            Publish(notification);
        }

        private bool TryInvalidateOwner(OwnedHost owner, out Notification notification)
        {
            lock (stateLock)
            {
                if (!ReferenceEquals(currentOwner, owner))
                {
                    notification = default;
                    return false;
                }

                _ = DetachCurrentOwnerLocked(out notification);
                return true;
            }
        }

        private OwnedHost? DetachCurrentOwnerLocked(out Notification notification)
        {
            var owner = currentOwner;
            if (owner == null)
            {
                notification = default;
                return null;
            }

            owner.ForwardingEnabled = false;
            currentOwner = null;
            latestSnapshot = null;
            sessionGeneration++;
            state = CaptionSourceState.Stopping;
            failureReason = null;
            notification = CreateNotificationLocked(
                null,
                new CaptionSourceStatus(
                    owner.SourceId,
                    CaptionSourceState.Stopping,
                    null,
                    DateTimeOffset.UtcNow),
                includeSnapshotInvalidation: true);
            return owner;
        }

        private CaptionSourceStartResult RecordFailure(
            CaptionSourceState failureState,
            string reason)
        {
            var result = CaptionSourceStartResult.Failed(failureState, reason);
            Notification notification;
            lock (stateLock)
            {
                state = result.State;
                failureReason = result.FailureReason;
                latestSnapshot = null;
                notification = CreateNotificationLocked(
                    null,
                    new CaptionSourceStatus(
                        CoordinatorSourceId,
                        result.State,
                        result.FailureReason,
                        DateTimeOffset.UtcNow));
            }
            Publish(notification);
            return result;
        }

        private void PublishTerminalState(string? cleanupFailure)
        {
            Notification notification;
            lock (stateLock)
            {
                state = cleanupFailure == null ?
                    CaptionSourceState.Stopped : CaptionSourceState.Faulted;
                failureReason = cleanupFailure;
                latestSnapshot = null;
                notification = CreateNotificationLocked(
                    null,
                    new CaptionSourceStatus(
                        CoordinatorSourceId,
                        state,
                        failureReason,
                        DateTimeOffset.UtcNow));
            }

            Publish(notification);
        }

        private void RecordSelectionCleanupFailure(string cleanupFailure)
        {
            lock (stateLock)
            {
                selectionCleanupFailure = CombineFailures(
                    selectionCleanupFailure,
                    cleanupFailure);
            }
        }

        private string? TakeSelectionCleanupFailure()
        {
            lock (stateLock)
            {
                var failure = selectionCleanupFailure;
                selectionCleanupFailure = null;
                return failure;
            }
        }

        private Func<ICaptionSource>? GetFactory(CaptionSourceKind kind) => kind switch
        {
            CaptionSourceKind.WindowsLiveCaptions => windowsLiveCaptionsFactory,
            CaptionSourceKind.LocalAsr => localAsrFactory,
            _ => null
        };

        private async Task<string?> CleanupOwnerAsync(OwnedHost owner)
        {
            var failures = new List<Exception>();
            try
            {
                await owner.Host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException("Caption-source host stop failed.", ex));
            }

            if (owner.StatusHandler != null)
                owner.Host.StatusChanged -= owner.StatusHandler;
            if (owner.SnapshotHandler != null)
                owner.Host.SnapshotChanged -= owner.SnapshotHandler;

            try
            {
                await owner.Host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException("Caption-source host disposal failed.", ex));
            }

            return FormatFailures(failures);
        }

        private static async Task<string?> CleanupHostAsync(CaptionSourceHost host)
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return $"Caption-source host cleanup failed: {ex.Message}";
            }
        }

        private static async Task<string?> CleanupSourceAsync(ICaptionSource source)
        {
            var failures = new List<Exception>();
            try { await source.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(new InvalidOperationException("Source stop failed.", ex)); }
            try { await source.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(new InvalidOperationException("Source disposal failed.", ex)); }
            return FormatFailures(failures);
        }

        private string? CancelSelectionOutsideLock(
            CancellationTokenSource? cancellationToCancel)
        {
            if (cancellationToCancel == null)
                return null;

            synchronousCancellationDepth++;
            try
            {
                cancellationToCancel.Cancel();
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (Exception ex)
            {
                return $"Caption-source selection cancellation failed: {FormatFailure(ex)}";
            }
            finally
            {
                synchronousCancellationDepth--;
            }
        }

        private bool IsReentrantLifecycleCall =>
            callbackDepth.Value != 0 || synchronousCancellationDepth != 0;

        private bool IsCurrentOwnerLocked(OwnedHost owner) =>
            ReferenceEquals(currentOwner, owner) && owner.ForwardingEnabled;

        private Notification CreateNotificationLocked(
            OwnedHost? owner,
            CaptionSourceStatus status,
            bool includeSnapshotInvalidation = false) =>
            new(++notificationVersion, owner, status, null, includeSnapshotInvalidation);

        private Notification CreateNotificationLocked(
            OwnedHost owner,
            AcceptedCaptionSnapshot? snapshot) =>
            new(++notificationVersion, owner, null, snapshot, true);

        private void Publish(Notification notification)
        {
            if (notification.Version == 0)
                return;

            if (notification.IncludeSnapshot)
                InvokeSafely(SnapshotChanged, notification.Snapshot, notification);
            if (notification.Status != null)
                InvokeSafely(StatusChanged, notification.Status, notification);
        }

        private void InvokeSafely<T>(
            EventHandler<T>? handlers,
            T eventData,
            Notification notification)
        {
            callbackDepth.Value++;
            try
            {
                foreach (EventHandler<T> handler in handlers?.GetInvocationList() ?? [])
                {
                    lock (stateLock)
                    {
                        if (notification.Version != notificationVersion)
                            return;
                        if (notification.Owner != null && !IsCurrentOwnerLocked(notification.Owner))
                            return;
                    }

                    try { handler(this, eventData); }
                    catch
                    {
                        // Application consumers must not corrupt source ownership.
                    }
                }
            }
            finally
            {
                callbackDepth.Value--;
            }
        }

        private void ThrowIfDisposeRequested()
        {
            if (disposeRequested)
                throw new ObjectDisposedException(nameof(CaptionSourceCoordinator));
        }

        private static string CombineFailure(string original, string? cleanup) =>
            string.IsNullOrWhiteSpace(cleanup) ? original : $"{original} {cleanup}";

        private static string? CombineFailures(params string?[] failures)
        {
            var retained = failures.Where(value => !string.IsNullOrWhiteSpace(value));
            var combined = string.Join(" ", retained);
            return string.IsNullOrWhiteSpace(combined) ? null : combined;
        }

        private static string? FormatFailures(List<Exception> failures) =>
            failures.Count == 0 ? null : string.Join(" ", failures.Select(FormatFailure));

        private static string FormatFailure(Exception failure) =>
            failure.InnerException == null
                ? failure.Message
                : $"{failure.Message} {failure.InnerException.Message}";

        private sealed class OwnedHost
        {
            internal OwnedHost(
                CaptionSourceKind kind,
                string sourceId,
                CaptionSourceHost host)
            {
                Kind = kind;
                SourceId = sourceId;
                Host = host;
            }

            internal CaptionSourceKind Kind { get; }
            internal string SourceId { get; }
            internal CaptionSourceHost Host { get; }
            internal bool ForwardingEnabled { get; set; } = true;
            internal long LastHostSessionGeneration { get; set; }
            internal CaptionSourceStartResult? StartResult { get; set; }
            internal EventHandler<CaptionSourceStatus>? StatusHandler { get; set; }
            internal EventHandler<AcceptedCaptionSnapshot?>? SnapshotHandler { get; set; }
        }

        private readonly record struct Notification(
            long Version,
            OwnedHost? Owner,
            CaptionSourceStatus? Status,
            AcceptedCaptionSnapshot? Snapshot,
            bool IncludeSnapshot);
    }
}
