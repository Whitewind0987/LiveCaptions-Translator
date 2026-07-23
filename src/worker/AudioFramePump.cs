using System.Threading.Channels;
using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.buffering;

namespace LiveCaptionsTranslator.worker
{
    public enum AudioFramePumpPhase { NotStarted, WaitingForFrame, WritingFrame, Completed, Canceling, Canceled, Faulted }

    public sealed record AudioFramePumpDiagnostics(
        AudioFramePumpPhase Phase,
        long FramesSent,
        long BytesSent,
        long SourceSequenceGaps,
        long? FirstSequence,
        long? CurrentSequence,
        long? LastSequence,
        bool SourceCompletionObserved,
        bool OwnedCancellationUsed,
        string? FailureReason);

    public sealed class AudioFramePump
    {
        private readonly BoundedAudioFrameBuffer source;
        private readonly IAsrWorkerTransport transport;
        private readonly Guid workerSessionId;
        private readonly Guid captureSessionId;
        private readonly long initialSequence;
        private readonly object diagnosticsLock = new();
        private long framesSent;
        private long bytesSent;
        private long sequenceGaps;
        private long? firstSequence;
        private long? currentSequence;
        private long? lastSequence;
        private int phase;
        private int sourceCompletionObserved;
        private int ownedCancellationUsed;
        private string? failureReason;

        public AudioFramePump(BoundedAudioFrameBuffer source, IAsrWorkerTransport transport, Guid workerSessionId, Guid captureSessionId, long initialSequence)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.workerSessionId = workerSessionId;
            this.captureSessionId = captureSessionId;
            if (initialSequence < 1) throw new ArgumentOutOfRangeException(nameof(initialSequence));
            this.initialSequence = initialSequence;
        }

        public AudioFramePumpDiagnostics Diagnostics
        {
            get
            {
                lock (diagnosticsLock)
                {
                    return new(
                        (AudioFramePumpPhase)Volatile.Read(ref phase),
                        Interlocked.Read(ref framesSent),
                        Interlocked.Read(ref bytesSent),
                        Interlocked.Read(ref sequenceGaps),
                        firstSequence,
                        currentSequence,
                        lastSequence,
                        Volatile.Read(ref sourceCompletionObserved) != 0,
                        Volatile.Read(ref ownedCancellationUsed) != 0,
                        Volatile.Read(ref failureReason));
                }
            }
        }

        public void MarkOwnedCancellationRequested(string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            Interlocked.Exchange(ref ownedCancellationUsed, 1);
            Interlocked.CompareExchange(ref failureReason, reason, null);
            while (true)
            {
                var current = Volatile.Read(ref phase);
                if (current >= (int)AudioFramePumpPhase.Completed)
                    break;
                if (Interlocked.CompareExchange(ref phase, (int)AudioFramePumpPhase.Canceling, current) == current)
                    break;
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                while (true)
                {
                    NormalizedAudioFrame frame;
                    Volatile.Write(ref phase, (int)AudioFramePumpPhase.WaitingForFrame);
                    try { frame = await source.ReadAsync(cancellationToken).ConfigureAwait(false); }
                    catch (ChannelClosedException)
                    {
                        Interlocked.Exchange(ref sourceCompletionObserved, 1);
                        Volatile.Write(ref phase, (int)AudioFramePumpPhase.Completed);
                        break;
                    }
                    lock (diagnosticsLock) currentSequence = frame.Sequence;
                    if (frame.SessionId != captureSessionId) throw new InvalidOperationException("A stale capture-session frame reached the worker pump.");
                    long? previousSequence;
                    lock (diagnosticsLock) previousSequence = lastSequence;
                    if (previousSequence.HasValue)
                    {
                        if (frame.Sequence <= previousSequence.Value) throw new InvalidOperationException("Audio frame order is not strictly increasing.");
                        if (frame.Sequence > previousSequence.Value + 1) Interlocked.Add(ref sequenceGaps, frame.Sequence - previousSequence.Value - 1);
                    }
                    else
                    {
                        if (frame.Sequence < initialSequence) throw new InvalidOperationException("Audio frame sequence precedes the stream's initial sequence.");
                        if (frame.Sequence > initialSequence) Interlocked.Add(ref sequenceGaps, frame.Sequence - initialSequence);
                    }
                    lock (diagnosticsLock) firstSequence ??= frame.Sequence;
                    Volatile.Write(ref phase, (int)AudioFramePumpPhase.WritingFrame);
                    await transport.SendAudioFrameAsync(workerSessionId, frame, cancellationToken).ConfigureAwait(false);
                    lock (diagnosticsLock) lastSequence = frame.Sequence;
                    Interlocked.Increment(ref framesSent);
                    Interlocked.Add(ref bytesSent, frame.Payload.Length);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref phase, (int)AudioFramePumpPhase.Canceled);
                throw;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Volatile.Write(ref phase, (int)AudioFramePumpPhase.Faulted);
                throw;
            }
        }
    }
}
