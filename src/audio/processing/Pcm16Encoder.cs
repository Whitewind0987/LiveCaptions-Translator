using System.Buffers.Binary;

namespace LiveCaptionsTranslator.audio.processing
{
    public static class Pcm16Encoder
    {
        public static short EncodeSample(float sample)
        {
            if (float.IsNaN(sample))
                sample = 0;
            sample = Math.Clamp(sample, -1f, 1f);

            if (sample >= 1f)
                return short.MaxValue;
            if (sample <= -1f)
                return short.MinValue;

            var scale = sample < 0 ? 32768f : 32767f;
            return (short)Math.Round(sample * scale, MidpointRounding.AwayFromZero);
        }

        public static byte[] Encode(ReadOnlySpan<float> samples)
        {
            var output = new byte[samples.Length * NormalizedAudioFormat.BytesPerSample];
            for (var index = 0; index < samples.Length; index++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    output.AsSpan(index * 2, 2),
                    EncodeSample(samples[index]));
            }
            return output;
        }
    }
}
