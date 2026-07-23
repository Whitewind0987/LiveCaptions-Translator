using System.Buffers.Binary;

using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.processing;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class StreamingAudioNormalizerTests
{
    [Fact]
    public void MonoFloat32DecodesToPcm16()
    {
        var normalizer = Normalizer(16000, 1, 32, AudioSampleEncoding.IeeeFloat);
        var output = normalizer.Process(FloatBytes(0f, 0.5f, -0.5f));
        Assert.Equal(new short[] { 0, 16384, -16384 }, DecodePcm16(output));
    }

    [Fact]
    public void StereoFloatUsesArithmeticMeanDownmix()
    {
        var normalizer = Normalizer(16000, 2, 32, AudioSampleEncoding.IeeeFloat);
        var output = normalizer.Process(FloatBytes(1f, -1f, 0.5f, 0.5f));
        Assert.Equal(new short[] { 0, 16384 }, DecodePcm16(output));
    }

    [Fact]
    public void Pcm16IsDecoded()
    {
        var output = Normalizer(16000, 1, 16, AudioSampleEncoding.PcmSignedInteger)
            .Process(AudioTestData.Pcm16(0, short.MaxValue, short.MinValue));
        Assert.Equal(new short[] { 0, 32766, -32768 }, DecodePcm16(output));
    }

    [Fact]
    public void Pcm24SignExtensionIsCorrect()
    {
        var input = new byte[] { 0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x80 };
        var output = Normalizer(16000, 1, 24, AudioSampleEncoding.PcmSignedInteger).Process(input);
        Assert.Equal(new short[] { 32767, -32768 }, DecodePcm16(output));
    }

    [Fact]
    public void Pcm32IsDecoded()
    {
        var input = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(0, 4), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(4, 4), int.MinValue);
        var output = Normalizer(16000, 1, 32, AudioSampleEncoding.PcmSignedInteger).Process(input);
        Assert.Equal(new short[] { 32767, -32768 }, DecodePcm16(output));
    }

    [Fact]
    public void DownmixClampsOutsideNormalizedRange()
    {
        var output = Normalizer(16000, 1, 32, AudioSampleEncoding.IeeeFloat)
            .Process(FloatBytes(2f, -2f));
        Assert.Equal(new short[] { 32767, -32768 }, DecodePcm16(output));
    }

    [Fact]
    public void UnsupportedEncodingIsTypedFailure()
    {
        var format = new AudioInputFormat(16000, 1, 8, 1, AudioSampleEncoding.PcmSignedInteger);
        var exception = Assert.Throws<NotSupportedException>(() => new StreamingAudioNormalizer(format));
        Assert.Contains("Unsupported", exception.Message);
    }

    [Fact]
    public void IncompleteInputFrameCarriesAcrossChunks()
    {
        var normalizer = Normalizer(16000, 2, 16, AudioSampleEncoding.PcmSignedInteger);
        var frame = AudioTestData.Pcm16(1000, 3000);
        Assert.Empty(normalizer.Process(frame.AsSpan(0, 3)));
        var output = normalizer.Process(frame.AsSpan(3));
        Assert.Equal(new short[] { 2000 }, DecodePcm16(output));
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void OneSecondResamplesToExpectedSampleCount(int inputRate)
    {
        var input = SineBytes(inputRate, 440, inputRate);
        var output = Normalizer(inputRate, 1, 32, AudioSampleEncoding.IeeeFloat).Process(input);
        Assert.InRange(output.Length / 2, 15999, 16001);
    }

    [Fact]
    public void IdentityRatePreservesSampleCountAndOrder()
    {
        var input = AudioTestData.Pcm16(-1000, 0, 1000, 2000);
        var output = Normalizer(16000, 1, 16, AudioSampleEncoding.PcmSignedInteger).Process(input);
        Assert.Equal(new short[] { -1000, 0, 1000, 2000 }, DecodePcm16(output));
    }

    [Theory]
    [InlineData(48000, 1)]
    [InlineData(44100, 2)]
    public void SplitChunksMatchOneLargeChunkExactly(int inputRate, int channels)
    {
        var mono = Enumerable.Range(0, inputRate / 10)
            .Select(index => (float)Math.Sin(index * 2 * Math.PI * 300 / inputRate))
            .ToArray();
        var interleaved = channels == 1
            ? mono
            : mono.SelectMany(sample => new[] { sample, sample * 0.5f }).ToArray();
        var bytes = FloatBytes(interleaved);

        var whole = Normalizer(inputRate, channels, 32, AudioSampleEncoding.IeeeFloat).Process(bytes);
        var splitNormalizer = Normalizer(inputRate, channels, 32, AudioSampleEncoding.IeeeFloat);
        var splitOutput = new List<byte>();
        var offsets = new[] { 1, 7, 13, 64, 3, 511 };
        var position = 0;
        var chunkIndex = 0;
        while (position < bytes.Length)
        {
            var length = Math.Min(offsets[chunkIndex++ % offsets.Length], bytes.Length - position);
            splitOutput.AddRange(splitNormalizer.Process(bytes.AsSpan(position, length)));
            position += length;
        }

        Assert.Equal(whole, splitOutput.ToArray());
    }

    [Fact]
    public void ResetPreventsPreviousSessionStateFromLeaking()
    {
        var normalizer = Normalizer(48000, 1, 32, AudioSampleEncoding.IeeeFloat);
        normalizer.Process(FloatBytes(1f));
        normalizer.Reset();
        var output = normalizer.Process(FloatBytes(0f, 0f, 0f, 0f));
        Assert.All(DecodePcm16(output), sample => Assert.Equal(0, sample));
    }

    private static StreamingAudioNormalizer Normalizer(
        int rate, int channels, int bits, AudioSampleEncoding encoding) =>
        new(new AudioInputFormat(rate, channels, bits, channels * bits / 8, encoding));

    private static byte[] FloatBytes(params float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * 4, 4),
                BitConverter.SingleToInt32Bits(samples[index]));
        }
        return bytes;
    }

    private static byte[] SineBytes(int sampleRate, double frequency, int count) =>
        FloatBytes(Enumerable.Range(0, count)
            .Select(index => (float)Math.Sin(index * 2 * Math.PI * frequency / sampleRate))
            .ToArray());

    private static short[] DecodePcm16(byte[] bytes) =>
        Enumerable.Range(0, bytes.Length / 2)
            .Select(index => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index * 2, 2)))
            .ToArray();
}
