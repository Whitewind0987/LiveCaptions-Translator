using System.Threading.Channels;

namespace LiveCaptionsTranslator.audio.buffering
{
    internal sealed record FramePublicationNotification(
        Guid SessionId,
        long Generation,
        NormalizedAudioFrame Frame);

    internal sealed class FramePublicationDispatcher : IAsyncDisposable
    {
        internal const int DefaultCapacity = 32;

        private readonly Channel<FramePublicationNotification> channel;
        private readonly Action<FramePublicationDispatcher, FramePublicationNotification> publish;
        private readonly Action<Exception> faulted;
        private readonly Action? beforeDispatch;
        private readonly TaskCompletionSource<object?> activation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AsyncLocal<bool> currentPublication = new();
        private readonly Task runner;
        private readonly Guid sessionId;
        private readonly long generation;
        private readonly int capacity;
        private int accepting = 1;
        private int queuedCount;
        private int maximumQueuedCount;
        private long droppedCount;
        private string? failureReason;
        private int disposeStarted;

        internal FramePublicationDispatcher(
            Guid sessionId,
            long generation,
            Action<FramePublicationDispatcher, FramePublicationNotification> publish,
            Action<Exception> faulted,
            Action? beforeDispatch = null,
            int capacity = DefaultCapacity)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentException("A publication session is required.", nameof(sessionId));
            if (generation < 1)
                throw new ArgumentOutOfRangeException(nameof(generation));
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            this.sessionId = sessionId;
            this.generation = generation;
            this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
            this.faulted = faulted ?? throw new ArgumentNullException(nameof(faulted));
            this.beforeDispatch = beforeDispatch;
            this.capacity = capacity;
            channel = Channel.CreateBounded<FramePublicationNotification>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            runner = RunAsync();
        }

        internal int Capacity => capacity;
        internal int QueuedCount => Math.Max(0, Volatile.Read(ref queuedCount));
        internal int MaximumQueuedCount => Volatile.Read(ref maximumQueuedCount);
        internal long DroppedCount => Interlocked.Read(ref droppedCount);
        internal bool IsCurrentPublication => currentPublication.Value;
        internal Task Completion => runner;

        internal void Activate() => activation.TrySetResult(null);

        internal bool TryEnqueue(FramePublicationNotification notification)
        {
            ArgumentNullException.ThrowIfNull(notification);
            if (!Matches(notification) || Volatile.Read(ref accepting) == 0)
                return false;
            if (!channel.Writer.TryWrite(notification))
            {
                Interlocked.Increment(ref droppedCount);
                return false;
            }

            var count = Interlocked.Increment(ref queuedCount);
            UpdateMaximum(count);
            return true;
        }

        internal bool CanPublish(FramePublicationNotification notification) =>
            Volatile.Read(ref accepting) != 0 && Matches(notification);

        internal void Invalidate()
        {
            Interlocked.Exchange(ref accepting, 0);
            channel.Writer.TryComplete();
            activation.TrySetResult(null);
        }

        internal async Task<string?> StopAsync()
        {
            Invalidate();
            if (!IsCurrentPublication)
                await runner.ConfigureAwait(false);
            return Volatile.Read(ref failureReason);
        }

        private async Task RunAsync()
        {
            try
            {
                await activation.Task.ConfigureAwait(false);
                while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var notification))
                    {
                        Interlocked.Decrement(ref queuedCount);
                        if (!CanPublish(notification))
                            continue;

                        var previous = currentPublication.Value;
                        currentPublication.Value = true;
                        try
                        {
                            beforeDispatch?.Invoke();
                            if (CanPublish(notification))
                                publish(this, notification);
                        }
                        finally
                        {
                            currentPublication.Value = previous;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failureReason = $"Frame notification dispatcher failed: {ex.Message}";
                Invalidate();
                try { faulted(ex); }
                catch (Exception callbackFailure)
                {
                    failureReason = $"{failureReason} Dispatcher fault handling failed: {callbackFailure.Message}";
                }
            }
            finally
            {
                while (channel.Reader.TryRead(out _))
                    Interlocked.Decrement(ref queuedCount);
            }
        }

        private bool Matches(FramePublicationNotification notification) =>
            notification.SessionId == sessionId && notification.Generation == generation;

        private void UpdateMaximum(int count)
        {
            var current = Volatile.Read(ref maximumQueuedCount);
            while (count > current)
            {
                var observed = Interlocked.CompareExchange(ref maximumQueuedCount, count, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
                return;
            await StopAsync().ConfigureAwait(false);
        }
    }
}
