namespace LiveCaptionsTranslator.audio.processing
{
    public sealed class AudioFrameAssembler
    {
        private readonly List<byte> remainder = [];
        private Guid sessionId;
        private long nextSequence;
        private long nextSampleIndex;

        public void StartSession(Guid newSessionId)
        {
            if (newSessionId == Guid.Empty)
                throw new ArgumentException("Audio session identity must not be empty.", nameof(newSessionId));

            sessionId = newSessionId;
            nextSequence = 1;
            nextSampleIndex = 0;
            remainder.Clear();
        }

        public IReadOnlyList<NormalizedAudioFrame> Append(
            ReadOnlySpan<byte> normalizedPcm,
            DateTimeOffset capturedAtUtc)
        {
            if (sessionId == Guid.Empty)
                throw new InvalidOperationException("StartSession must be called before appending audio.");
            if (normalizedPcm.Length % NormalizedAudioFormat.BytesPerSample != 0)
                throw new ArgumentException("Normalized PCM chunks must contain complete 16-bit samples.",
                    nameof(normalizedPcm));

            remainder.AddRange(normalizedPcm.ToArray());
            var frames = new List<NormalizedAudioFrame>();
            while (remainder.Count >= NormalizedAudioFormat.BytesPerFrame)
            {
                var payload = remainder.GetRange(0, NormalizedAudioFormat.BytesPerFrame).ToArray();
                remainder.RemoveRange(0, NormalizedAudioFormat.BytesPerFrame);
                frames.Add(new NormalizedAudioFrame(
                    sessionId,
                    nextSequence++,
                    nextSampleIndex,
                    capturedAtUtc,
                    payload));
                nextSampleIndex += NormalizedAudioFormat.SamplesPerFrame;
            }

            return frames;
        }

        public void Reset()
        {
            remainder.Clear();
            sessionId = Guid.Empty;
            nextSequence = 0;
            nextSampleIndex = 0;
        }

        public int RemainderBytes => remainder.Count;
    }
}
