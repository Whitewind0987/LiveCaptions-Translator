using System.Threading.Channels;

namespace LiveCaptionsTranslator.audio.buffering
{
    public sealed class BoundedAudioFrameBuffer : IDisposable
    {
        public const int DefaultCapacity = 250;

        private readonly Queue<NormalizedAudioFrame> frames = [];
        private readonly object stateLock = new();
        private TaskCompletionSource stateChanged = CreateSignal();
        private bool completed;
        private long droppedCount;
        private long consumedCount;
        private bool disposed;

        public BoundedAudioFrameBuffer(int capacity = DefaultCapacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
        }

        public int Capacity { get; }
        public long DroppedCount { get { lock (stateLock) return droppedCount; } }
        public long ConsumedCount { get { lock (stateLock) return consumedCount; } }
        public int Count { get { lock (stateLock) return frames.Count; } }
        public bool IsCompleted { get { lock (stateLock) return completed && frames.Count == 0; } }

        public bool TryWrite(NormalizedAudioFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            lock (stateLock)
            {
                if (completed || disposed)
                    return false;

                if (frames.Count == Capacity)
                {
                    frames.Dequeue();
                    droppedCount++;
                }
                frames.Enqueue(frame);
                SignalStateChanged();
                return true;
            }
        }

        public bool TryRead(out NormalizedAudioFrame? frame)
        {
            lock (stateLock)
            {
                if (frames.Count == 0)
                {
                    frame = null;
                    return false;
                }

                frame = frames.Dequeue();
                consumedCount++;
                return true;
            }
        }

        public async ValueTask<NormalizedAudioFrame> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task waitForChange;
                lock (stateLock)
                {
                    if (frames.Count > 0)
                    {
                        var frame = frames.Dequeue();
                        consumedCount++;
                        return frame;
                    }
                    if (completed || disposed)
                        throw new ChannelClosedException();
                    waitForChange = stateChanged.Task;
                }
                await waitForChange.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void Complete()
        {
            lock (stateLock)
            {
                if (completed)
                    return;
                completed = true;
                SignalStateChanged();
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed)
                    return;
                disposed = true;
                completed = true;
                frames.Clear();
                SignalStateChanged();
            }
        }

        private static TaskCompletionSource CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void SignalStateChanged()
        {
            var signal = stateChanged;
            stateChanged = CreateSignal();
            signal.TrySetResult();
        }
    }
}
