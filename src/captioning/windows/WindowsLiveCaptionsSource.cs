namespace LiveCaptionsTranslator.captioning.windows
{
    public sealed class WindowsLiveCaptionsSource : ICaptionSource, INativeCaptionWindowControl
    {
        internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
        internal static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

        private readonly ILiveCaptionsRuntime runtime;
        private readonly ICaptionSourceDelay delay;
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);
        private readonly object stateLock = new();

        private CaptionSourceState state = CaptionSourceState.Stopped;
        private string? failureReason;
        private Guid? sessionId;
        private long sequence;
        private long revision;
        private string? previousSnapshot;
        private CancellationTokenSource? pollingCancellation;
        private Task? pollingTask;
        private Task? stoppingTask;
        private int disposeStarted;

        public WindowsLiveCaptionsSource()
            : this(new LiveCaptionsRuntime(), new SystemCaptionSourceDelay())
        {
        }

        internal WindowsLiveCaptionsSource(
            ILiveCaptionsRuntime runtime,
            ICaptionSourceDelay delay)
        {
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        }

        public string SourceId => "windows-live-captions";

        public CaptionSourceState State
        {
            get
            {
                lock (stateLock)
                    return state;
            }
        }

        public string? FailureReason
        {
            get
            {
                lock (stateLock)
                    return failureReason;
            }
        }

        public bool IsWindowAvailable => runtime.IsWindowAvailable;
        public bool? IsWindowVisible => runtime.IsWindowVisible;

        public event EventHandler<CaptionEvent>? CaptionEventReceived;
        public event EventHandler<CaptionSourceStatus>? StatusChanged;

        public async Task<CaptionSourceStartResult> StartAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposing();
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var gateHeld = true;
            try
            {
                ThrowIfDisposing();

                if (State is CaptionSourceState.Running or CaptionSourceState.Restarting)
                    return CaptionSourceStartResult.Started(sessionId!.Value);

                if (State == CaptionSourceState.Stopping && stoppingTask != null)
                {
                    var pendingStop = stoppingTask;
                    lifecycleGate.Release();
                    gateHeld = false;
                    await pendingStop.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return await StartAsync(cancellationToken).ConfigureAwait(false);
                }

                SetStatus(CaptionSourceState.Starting, null);

                LiveCaptionsRuntimeInitializationResult initialization;
                try
                {
                    initialization = await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await CleanupAfterFailedInitializationAsync(null).ConfigureAwait(false);
                    SetStatus(CaptionSourceState.Stopped, null);
                    throw;
                }
                catch (Exception ex)
                {
                    initialization = LiveCaptionsRuntimeInitializationResult.Failed(
                        LiveCaptionsRuntimeFailureCategory.Faulted,
                        $"Live Captions initialization failed: {ex.Message}");
                }

                if (!initialization.Success)
                {
                    var reason = await CleanupAfterFailedInitializationAsync(
                        initialization.FailureReason).ConfigureAwait(false);
                    var failedState = MapFailure(initialization.FailureCategory);
                    SetStatus(failedState, reason);
                    return CaptionSourceStartResult.Failed(failedState, reason);
                }

                pollingCancellation?.Dispose();
                pollingCancellation = new CancellationTokenSource();
                EstablishNewSession();
                SetStatus(CaptionSourceState.Running, null);
                pollingTask = Task.Run(() => PollAsync(pollingCancellation.Token), CancellationToken.None);
                return CaptionSourceStartResult.Started(sessionId!.Value);
            }
            finally
            {
                if (gateHeld)
                    lifecycleGate.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task stopTask;
            try
            {
                if (State == CaptionSourceState.Stopped && stoppingTask == null)
                    return;

                if (stoppingTask != null)
                {
                    stopTask = stoppingTask;
                }
                else
                {
                    SetStatus(CaptionSourceState.Stopping, FailureReason);
                    pollingCancellation?.Cancel();
                    stopTask = StopCoreAsync(pollingTask, pollingCancellation);
                    stoppingTask = stopTask;
                }
            }
            finally
            {
                lifecycleGate.Release();
            }

            await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<CaptionWindowControlResult> ShowAsync(CancellationToken cancellationToken = default) =>
            ControlWindowSafelyAsync(show: true, cancellationToken);

        public Task<CaptionWindowControlResult> HideAsync(CancellationToken cancellationToken = default) =>
            ControlWindowSafelyAsync(show: false, cancellationToken);

        private async Task<CaptionWindowControlResult> ControlWindowSafelyAsync(
            bool show,
            CancellationToken cancellationToken)
        {
            if (State is not CaptionSourceState.Running and not CaptionSourceState.Restarting)
            {
                return CaptionWindowControlResult.Failed(
                    CaptionWindowControlFailure.Unavailable,
                    "The caption source does not currently have a native window.");
            }

            try
            {
                return show
                    ? await runtime.ShowAsync(cancellationToken).ConfigureAwait(false)
                    : await runtime.HideAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return CaptionWindowControlResult.Failed(
                    CaptionWindowControlFailure.Cancelled, "The window operation was cancelled.");
            }
            catch (Exception ex)
            {
                return CaptionWindowControlResult.Failed(
                    CaptionWindowControlFailure.Faulted,
                    $"The native caption-window operation failed: {ex.Message}");
            }
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var result = await runtime.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    switch (result.Status)
                    {
                        case LiveCaptionsSnapshotReadStatus.Success:
                            EmitChangedSnapshot(result.Text!);
                            break;
                        case LiveCaptionsSnapshotReadStatus.NoText:
                            break;
                        case LiveCaptionsSnapshotReadStatus.WindowLost:
                            if (!await TryRestartAsync(result.FailureReason, cancellationToken).ConfigureAwait(false))
                                return;
                            break;
                        case LiveCaptionsSnapshotReadStatus.Faulted:
                            SetStatus(
                                CaptionSourceState.Faulted,
                                result.FailureReason ?? "Live Captions snapshot reading failed.");
                            return;
                        case LiveCaptionsSnapshotReadStatus.Cancelled:
                            return;
                    }

                    await delay.DelayAsync(PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    SetStatus(CaptionSourceState.Faulted, $"Live Captions polling failed: {ex.Message}");
            }
        }

        private async Task<bool> TryRestartAsync(
            string? lossReason,
            CancellationToken cancellationToken)
        {
            SetStatus(
                CaptionSourceState.Restarting,
                lossReason ?? "The Live Captions window was unexpectedly closed.");

            try
            {
                await delay.DelayAsync(RestartDelay, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            LiveCaptionsRuntimeInitializationResult initialization;
            try
            {
                initialization = await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                initialization = LiveCaptionsRuntimeInitializationResult.Failed(
                    LiveCaptionsRuntimeFailureCategory.Faulted,
                    $"Live Captions restart failed: {ex.Message}");
            }

            if (!initialization.Success)
            {
                var reason = await CleanupAfterFailedInitializationAsync(
                    initialization.FailureReason).ConfigureAwait(false);
                SetStatus(MapFailure(initialization.FailureCategory), reason);
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
                return false;

            EstablishNewSession();
            SetStatus(CaptionSourceState.Running, null);
            return true;
        }

        private void EstablishNewSession()
        {
            lock (stateLock)
            {
                sessionId = Guid.NewGuid();
                sequence = 1;
                revision = 0;
                previousSnapshot = null;
            }

            PublishCaptionEvent(new CaptionEvent(
                CaptionEvent.CurrentSchemaVersion,
                sessionId.Value,
                1,
                0,
                0,
                CaptionEventKind.Reset,
                string.Empty,
                null,
                null,
                DateTimeOffset.UtcNow));
        }

        private void EmitChangedSnapshot(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            CaptionEvent? captionEvent = null;
            lock (stateLock)
            {
                if (state != CaptionSourceState.Running ||
                    string.Equals(previousSnapshot, text, StringComparison.Ordinal) ||
                    !sessionId.HasValue)
                {
                    return;
                }

                previousSnapshot = text;
                sequence++;
                revision++;
                captionEvent = new CaptionEvent(
                    CaptionEvent.CurrentSchemaVersion,
                    sessionId.Value,
                    sequence,
                    1,
                    revision,
                    CaptionEventKind.Partial,
                    text,
                    null,
                    null,
                    DateTimeOffset.UtcNow);
            }

            PublishCaptionEvent(captionEvent);
        }

        private async Task StopCoreAsync(Task? activePollingTask, CancellationTokenSource? cancellation)
        {
            string? cleanupFailure = null;
            try
            {
                if (activePollingTask != null)
                    await activePollingTask.ConfigureAwait(false);

                var cleanup = await runtime.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                cleanupFailure = cleanup.Success ? null : cleanup.FailureReason;
            }
            catch (Exception ex)
            {
                cleanupFailure = $"Failed to stop Live Captions cleanly: {ex.Message}";
            }
            finally
            {
                cancellation?.Dispose();
                lock (stateLock)
                {
                    pollingCancellation = null;
                    pollingTask = null;
                    sessionId = null;
                    sequence = 0;
                    revision = 0;
                    previousSnapshot = null;
                }

                SetStatus(CaptionSourceState.Stopped, cleanupFailure);

                await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    stoppingTask = null;
                }
                finally
                {
                    lifecycleGate.Release();
                }
            }
        }

        private async Task<string> CleanupAfterFailedInitializationAsync(string? originalReason)
        {
            var reason = string.IsNullOrWhiteSpace(originalReason)
                ? "Live Captions initialization failed without a reason."
                : originalReason;

            try
            {
                var cleanup = await runtime.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                if (!cleanup.Success && !string.IsNullOrWhiteSpace(cleanup.FailureReason))
                    reason = $"{reason} {cleanup.FailureReason}";
            }
            catch (Exception ex)
            {
                reason = $"{reason} Cleanup failed: {ex.Message}";
            }

            return reason;
        }

        private void SetStatus(CaptionSourceState newState, string? reason)
        {
            CaptionSourceStatus status;
            lock (stateLock)
            {
                state = newState;
                failureReason = reason;
                status = new CaptionSourceStatus(SourceId, newState, reason, DateTimeOffset.UtcNow);
            }

            InvokeSafely(StatusChanged, status);
        }

        private void PublishCaptionEvent(CaptionEvent captionEvent) =>
            InvokeSafely(CaptionEventReceived, captionEvent);

        private void InvokeSafely<T>(EventHandler<T>? handlers, T eventData)
        {
            if (handlers == null)
                return;

            foreach (EventHandler<T> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, eventData);
                }
                catch
                {
                    // One subscriber must not disrupt source lifecycle or polling.
                }
            }
        }

        private static CaptionSourceState MapFailure(
            LiveCaptionsRuntimeFailureCategory? category) =>
            category == LiveCaptionsRuntimeFailureCategory.Unavailable
                ? CaptionSourceState.Unavailable
                : CaptionSourceState.Faulted;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
                return;

            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Dispose();
            }
        }

        private void ThrowIfDisposing()
        {
            if (Volatile.Read(ref disposeStarted) != 0)
                throw new ObjectDisposedException(nameof(WindowsLiveCaptionsSource));
        }
    }
}
