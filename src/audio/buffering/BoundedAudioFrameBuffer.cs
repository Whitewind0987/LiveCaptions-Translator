using System.Threading.Channels;

namespace LiveCaptionsTranslator.audio.buffering
{
    public sealed class BoundedAudioFrameBuffer : IDisposable
    {
        public const int DefaultCapacity = 250;

        private readonly Queue<NormalizedAudioFrame> frames = [];
        private readonly SemaphoreSlim available = new(0);
        private readonly object stateLock = new();
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
                else
                {
                    available.Release();
                }

                frames.Enqueue(frame);
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
                _ = available.Wait(0);
                return true;
            }
        }

        public async ValueTask<NormalizedAudioFrame> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                await available.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                }
            }
        }

        public void Complete()
        {
            lock (stateLock)
            {
                if (completed)
                    return;
                completed = true;
                available.Release();
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
                available.Release();
            }
        }
    }
}
