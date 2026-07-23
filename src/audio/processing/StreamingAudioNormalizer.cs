using System.Buffers.Binary;

namespace LiveCaptionsTranslator.audio.processing
{
    public sealed class StreamingAudioNormalizer
    {
        private readonly AudioInputFormat format;
        private readonly List<float> resampleBuffer = [];
        private byte[] inputRemainder = [];
        private long bufferStartSampleIndex;
        private double nextOutputPosition;

        public StreamingAudioNormalizer(AudioInputFormat format)
        {
            this.format = format ?? throw new ArgumentNullException(nameof(format));
            ValidateSupportedFormat(format);
        }

        public byte[] Process(ReadOnlySpan<byte> input)
        {
            if (input.Length == 0)
                return [];

            var combined = new byte[inputRemainder.Length + input.Length];
            inputRemainder.CopyTo(combined, 0);
            input.CopyTo(combined.AsSpan(inputRemainder.Length));

            var completeLength = combined.Length - combined.Length % format.BlockAlign;
            inputRemainder = combined.AsSpan(completeLength).ToArray();
            if (completeLength == 0)
                return [];

            var mono = DecodeAndDownmix(combined.AsSpan(0, completeLength));
            if (format.SampleRate == NormalizedAudioFormat.SampleRate)
                return Pcm16Encoder.Encode(mono);

            resampleBuffer.AddRange(mono);
            var output = ResampleAvailable();
            return Pcm16Encoder.Encode(output);
        }

        public void Reset()
        {
            inputRemainder = [];
            resampleBuffer.Clear();
            bufferStartSampleIndex = 0;
            nextOutputPosition = 0;
        }

        private float[] DecodeAndDownmix(ReadOnlySpan<byte> input)
        {
            var frameCount = input.Length / format.BlockAlign;
            var mono = new float[frameCount];
            var bytesPerSample = format.BitsPerSample / 8;

            for (var frame = 0; frame < frameCount; frame++)
            {
                double sum = 0;
                var frameOffset = frame * format.BlockAlign;
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var sampleOffset = frameOffset + channel * bytesPerSample;
                    sum += DecodeSample(input.Slice(sampleOffset, bytesPerSample));
                }

                mono[frame] = Math.Clamp((float)(sum / format.Channels), -1f, 1f);
            }

            return mono;
        }

        private float DecodeSample(ReadOnlySpan<byte> sample) =>
            format.Encoding switch
            {
                AudioSampleEncoding.IeeeFloat when format.BitsPerSample == 32 =>
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample)),
                AudioSampleEncoding.PcmSignedInteger when format.BitsPerSample == 16 =>
                    BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                AudioSampleEncoding.PcmSignedInteger when format.BitsPerSample == 24 =>
                    DecodePcm24(sample) / 8388608f,
                AudioSampleEncoding.PcmSignedInteger when format.BitsPerSample == 32 =>
                    BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
                _ => throw new NotSupportedException(UnsupportedFormatMessage(format))
            };

        private float[] ResampleAvailable()
        {
            if (resampleBuffer.Count < 2)
                return [];

            var step = (double)format.SampleRate / NormalizedAudioFormat.SampleRate;
            var lastAvailableIndex = bufferStartSampleIndex + resampleBuffer.Count - 1;
            var output = new List<float>();

            while (nextOutputPosition < lastAvailableIndex)
            {
                var leftAbsolute = (long)Math.Floor(nextOutputPosition);
                var fraction = nextOutputPosition - leftAbsolute;
                var left = (int)(leftAbsolute - bufferStartSampleIndex);
                var value = resampleBuffer[left] +
                    (resampleBuffer[left + 1] - resampleBuffer[left]) * (float)fraction;
                output.Add(float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f);
                nextOutputPosition += step;
            }

            var retainFrom = Math.Max(bufferStartSampleIndex, (long)Math.Floor(nextOutputPosition) - 1);
            var removeCount = (int)(retainFrom - bufferStartSampleIndex);
            if (removeCount > 0)
            {
                resampleBuffer.RemoveRange(0, removeCount);
                bufferStartSampleIndex += removeCount;
            }

            return output.ToArray();
        }

        private static int DecodePcm24(ReadOnlySpan<byte> sample)
        {
            var value = sample[0] | sample[1] << 8 | sample[2] << 16;
            if ((value & 0x800000) != 0)
                value |= unchecked((int)0xFF000000);
            return value;
        }

        private static void ValidateSupportedFormat(AudioInputFormat format)
        {
            var supported =
                format.Encoding == AudioSampleEncoding.IeeeFloat && format.BitsPerSample == 32 ||
                format.Encoding == AudioSampleEncoding.PcmSignedInteger &&
                format.BitsPerSample is 16 or 24 or 32;
            if (!supported)
                throw new NotSupportedException(UnsupportedFormatMessage(format));
        }

        private static string UnsupportedFormatMessage(AudioInputFormat format) =>
            $"Unsupported loopback format: {format}. Supported formats are float32 and signed PCM16/24/32.";
    }
}
