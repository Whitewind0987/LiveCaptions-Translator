using System.Text;
using LiveCaptionsTranslator.tools.asrworkerprobe;
using LiveCaptionsTranslator.worker;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class Stage5ProbeAndConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"lct-stage5-{Guid.NewGuid():N}");
    public Stage5ProbeAndConfigurationTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void TransportOnlyConfigurationIsStable()
    {
        var value = WorkerRecognitionConfiguration.Disabled;
        Assert.False(value.Enabled); Assert.Equal("auto", value.Language);
        Assert.InRange(value.ThreadCount, 1, WorkerRecognitionConfiguration.MaximumThreadCount);
    }

    [Fact]
    public void RecognitionConfigurationNormalizesTypedValues()
    {
        var (vad, whisper) = Models();
        var value = WorkerRecognitionConfiguration.Create(vad, whisper, " EN ", 3);
        Assert.True(value.Enabled); Assert.Equal("en", value.Language); Assert.Equal(3, value.ThreadCount);
        Assert.True(Path.IsPathFullyQualified(value.VadModelPath!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void RecognitionConfigurationRejectsInvalidThreadCount(int threads)
    {
        var (vad, whisper) = Models();
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerRecognitionConfiguration.Create(vad, whisper, "auto", threads));
    }

    [Fact]
    public void RecognitionConfigurationRejectsRelativePath() =>
        Assert.Throws<ArgumentException>(() => WorkerRecognitionConfiguration.Create("vad.onnx", "whisper.bin"));

    [Fact]
    public void ProbeRequiresBothModels()
    {
        var error = Assert.Throws<ArgumentException>(() => ProbeOptionsParser.Parse([
            "--worker", "worker.exe", "--audio", "--recognition", "--vad-model", "vad.onnx"]));
        Assert.Contains("both", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProbeRejectsDuplicateOption() => Assert.Throws<ArgumentException>(() => ProbeOptionsParser.Parse([
        "--worker", "worker.exe", "--synthetic", "--duration", "1", "--duration", "2"]));

    [Fact]
    public void ExpectedTokenMatchingNormalizesCasePunctuationAndWhitespace()
    {
        Assert.True(ExpectedCaptionMatcher.Contains("Structured,  CAPTION\tEvents!", "structured caption events"));
        Assert.False(ExpectedCaptionMatcher.Contains("unrelated transcript", "structured caption events"));
    }

    [Fact]
    public void WaveReaderFindsDataAfterUnknownChunkAndPadsOneFinalFrame()
    {
        var path = Path.Combine(directory, "valid.wav");
        File.WriteAllBytes(path, Wave(extraChunk: true, pcmBytes: 642));
        var fixture = WaveFixtureReader.Read(path);
        Assert.Equal(2, fixture.FrameCount); Assert.Equal(638, fixture.PaddedBytes);
    }

    [Theory]
    [InlineData(3, 16000, 1, 16, "WAV format must be PCM format 1.")]
    [InlineData(1, 8000, 1, 16, "WAV sample rate must be 16000 Hz.")]
    [InlineData(1, 16000, 2, 16, "WAV must be mono.")]
    [InlineData(1, 16000, 1, 8, "WAV must use 16-bit little-endian samples.")]
    public void WaveReaderRejectsUnsupportedFormat(ushort format, uint rate, ushort channels, ushort bits, string message)
    {
        var path = Path.Combine(directory, "invalid.wav");
        File.WriteAllBytes(path, Wave(false, 640, format, rate, channels, bits));
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => WaveFixtureReader.Read(path)).Message);
    }

    private (string Vad, string Whisper) Models()
    {
        var vad = Path.Combine(directory, "vad.onnx"); var whisper = Path.Combine(directory, "whisper.bin");
        File.WriteAllText(vad, "model"); File.WriteAllText(whisper, "model"); return (vad, whisper);
    }

    private static byte[] Wave(bool extraChunk, int pcmBytes, ushort format = 1, uint rate = 16000, ushort channels = 1, ushort bits = 16)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write((uint)0); writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt ")); writer.Write((uint)16); writer.Write(format); writer.Write(channels); writer.Write(rate);
        writer.Write(rate * channels * bits / 8); writer.Write((ushort)(channels * bits / 8)); writer.Write(bits);
        if (extraChunk) { writer.Write(Encoding.ASCII.GetBytes("JUNK")); writer.Write((uint)3); writer.Write(new byte[3]); writer.Write((byte)0); }
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write((uint)pcmBytes); writer.Write(new byte[pcmBytes]);
        writer.Flush(); output.Position = 4; writer.Write((uint)(output.Length - 8)); writer.Flush(); return output.ToArray();
    }
}
