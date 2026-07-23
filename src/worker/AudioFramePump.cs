using System.Threading.Channels;
using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.buffering;

namespace LiveCaptionsTranslator.worker
{
    public sealed record AudioFramePumpDiagnostics(long FramesSent, long BytesSent, long SourceSequenceGaps, long? FirstSequence, long? LastSequence, string? FailureReason);

    public sealed class AudioFramePump
    {
        private readonly BoundedAudioFrameBuffer source;
        private readonly IAsrWorkerTransport transport;
        private readonly Guid workerSessionId;
        private readonly Guid captureSessionId;
        private long framesSent;
        private long bytesSent;
        private long sequenceGaps;
        private long? firstSequence;
        private long? lastSequence;
        private string? failureReason;

        public AudioFramePump(BoundedAudioFrameBuffer source, IAsrWorkerTransport transport, Guid workerSessionId, Guid captureSessionId)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.workerSessionId = workerSessionId;
            this.captureSessionId = captureSessionId;
        }

        public AudioFramePumpDiagnostics Diagnostics => new(Interlocked.Read(ref framesSent), Interlocked.Read(ref bytesSent), Interlocked.Read(ref sequenceGaps), firstSequence, lastSequence, Volatile.Read(ref failureReason));

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                while (true)
                {
                    NormalizedAudioFrame frame;
                    try { frame = await source.ReadAsync(cancellationToken).ConfigureAwait(false); }
                    catch (ChannelClosedException) { break; }
                    if (frame.SessionId != captureSessionId) throw new InvalidOperationException("A stale capture-session frame reached the worker pump.");
                    if (lastSequence.HasValue)
                    {
                        if (frame.Sequence <= lastSequence.Value) throw new InvalidOperationException("Audio frame order is not strictly increasing.");
                        if (frame.Sequence > lastSequence.Value + 1) Interlocked.Add(ref sequenceGaps, frame.Sequence - lastSequence.Value - 1);
                    }
                    firstSequence ??= frame.Sequence;
                    await transport.SendAudioFrameAsync(workerSessionId, frame, cancellationToken).ConfigureAwait(false);
                    lastSequence = frame.Sequence;
                    Interlocked.Increment(ref framesSent);
                    Interlocked.Add(ref bytesSent, frame.Payload.Length);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { failureReason = ex.Message; throw; }
        }
    }
}
