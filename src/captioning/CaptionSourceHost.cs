using LiveCaptionsTranslator.captioning.windows;

namespace LiveCaptionsTranslator.captioning
{
    public sealed record AcceptedCaptionSnapshot(
        Guid SessionId,
        long Sequence,
        long SegmentId,
        long Revision,
        string Text,
        long SessionGeneration);

    public sealed record CaptionSourceLatestState(
        long SessionGeneration,
        AcceptedCaptionSnapshot? Snapshot);

    public sealed class CaptionSourceHost : IAsyncDisposable
    {
        private readonly ICaptionSource source;
        private readonly CaptionEventGate gate = new();
        private readonly object stateLock = new();
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);

        private AcceptedCaptionSnapshot? latestSnapshot;
        private CaptionSourceStatus? latestStatus;
        private CaptionSourceStartResult? startResult;
        private string? lastGateRejectionReason;
        private long sessionGeneration;
        private bool startInvoked;
        private bool stopInvoked;
        private int disposeStarted;

        public CaptionSourceHost(ICaptionSource source)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            source.CaptionEventReceived += OnCaptionEventReceived;
            source.StatusChanged += OnStatusChanged;
        }

        public CaptionSourceState State
        {
            get
            {
                lock (stateLock)
                    return latestStatus?.State ?? source.State;
            }
        }

        public string? FailureReason
        {
            get
            {
                lock (stateLock)
                    return latestStatus?.FailureReason ?? source.FailureReason;
            }
        }

        public AcceptedCaptionSnapshot? LatestSnapshot
        {
            get
            {
                lock (stateLock)
                    return latestSnapshot;
            }
        }

        public long SessionGeneration
        {
            get
            {
                lock (stateLock)
                    return sessionGeneration;
            }
        }

        public CaptionSourceLatestState ReadLatestState()
        {
            lock (stateLock)
                return new CaptionSourceLatestState(sessionGeneration, latestSnapshot);
        }

        public Guid? ActiveSessionId
        {
            get
            {
                lock (stateLock)
                    return gate.ActiveSessionId;
            }
        }

        public long LastAcceptedSequence
        {
            get
            {
                lock (stateLock)
                    return gate.LastAcceptedSequence;
            }
        }

        public long HighestObservedSequence
        {
            get
            {
                lock (stateLock)
                    return gate.HighestObservedSequence;
            }
        }

        public long? CurrentSegmentId
        {
            get
            {
                lock (stateLock)
                    return gate.CurrentSegmentId;
            }
        }

        public long? CurrentRevision
        {
            get
            {
                lock (stateLock)
                    return gate.CurrentRevision;
            }
        }

        public string? LastGateRejectionReason
        {
            get
            {
                lock (stateLock)
                    return lastGateRejectionReason;
            }
        }

        public INativeCaptionWindowControl? NativeWindowControl =>
            source as INativeCaptionWindowControl;

        public event EventHandler<CaptionSourceStatus>? StatusChanged;
        public event EventHandler<AcceptedCaptionSnapshot?>? SnapshotChanged;

        public async Task<CaptionSourceStartResult> StartAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (startInvoked)
                    return startResult!;

                startInvoked = true;
                startResult = await source.StartAsync(cancellationToken).ConfigureAwait(false);
                return startResult;
            }
            catch
            {
                startInvoked = false;
                throw;
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
                if (stopInvoked)
                    return;

                stopInvoked = true;
                await source.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        private void OnCaptionEventReceived(object? sender, CaptionEvent captionEvent)
        {
            AcceptedCaptionSnapshot? snapshotToPublish;
            lock (stateLock)
            {
                if (!gate.TryAccept(captionEvent, out var rejectionReason))
                {
                    lastGateRejectionReason = rejectionReason;
                    return;
                }

                lastGateRejectionReason = null;
                if (captionEvent.Kind == CaptionEventKind.Reset)
                {
                    sessionGeneration++;
                    latestSnapshot = null;
                }
                else
                {
                    latestSnapshot = new AcceptedCaptionSnapshot(
                        captionEvent.SessionId,
                        captionEvent.Sequence,
                        captionEvent.SegmentId,
                        captionEvent.Revision,
                        captionEvent.Text,
                        sessionGeneration);
                }

                snapshotToPublish = latestSnapshot;
            }

            InvokeSafely(SnapshotChanged, snapshotToPublish);
        }

        private void OnStatusChanged(object? sender, CaptionSourceStatus status)
        {
            lock (stateLock)
                latestStatus = status;

            InvokeSafely(StatusChanged, status);
        }

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
                    // Host state must not depend on downstream subscribers.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
                return;

            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
                source.CaptionEventReceived -= OnCaptionEventReceived;
                source.StatusChanged -= OnStatusChanged;
                await source.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposeStarted) != 0)
                throw new ObjectDisposedException(nameof(CaptionSourceHost));
        }
    }
}
