using LiveCaptionsTranslator.audio.processing;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class Pcm16EncoderTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 16384)]
    [InlineData(-0.5f, -16384)]
    [InlineData(1f, 32767)]
    [InlineData(-1f, -32768)]
    [InlineData(2f, 32767)]
    [InlineData(-2f, -32768)]
    public void SamplesUseDocumentedClippingAndRounding(float input, short expected)
    {
        Assert.Equal(expected, Pcm16Encoder.EncodeSample(input));
    }

    [Fact]
    public void EncodingIsLittleEndianAndPositiveFullScaleDoesNotOverflow()
    {
        Assert.Equal(new byte[] { 0x34, 0x12, 0xFF, 0x7F },
            Pcm16Encoder.Encode([0x1234 / 32767f, 1f]));
    }
}
