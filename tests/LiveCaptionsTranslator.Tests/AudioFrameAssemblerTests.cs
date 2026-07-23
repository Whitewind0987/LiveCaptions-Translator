using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.processing;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class AudioFrameAssemblerTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExactFrameHasRequiredPayloadSequenceAndSampleIndex()
    {
        var assembler = Started();
        var frame = Assert.Single(assembler.Append(new byte[640], Timestamp));
        Assert.Equal(640, frame.Payload.Length);
        Assert.Equal(1, frame.Sequence);
        Assert.Equal(0, frame.StartSampleIndex);
        Assert.Equal(Timestamp, frame.CapturedAtUtc);
    }

    [Fact]
    public void ArbitraryChunksRetainRemainderAndProduceMonotonicFrames()
    {
        var assembler = Started();
        Assert.Empty(assembler.Append(new byte[300], Timestamp));
        var frames = assembler.Append(new byte[980], Timestamp);
        Assert.Equal(2, frames.Count);
        Assert.Equal(new long[] { 1, 2 }, frames.Select(frame => frame.Sequence));
        Assert.Equal(new long[] { 0, 320 }, frames.Select(frame => frame.StartSampleIndex));
        Assert.Equal(0, assembler.RemainderBytes);
    }

    [Fact]
    public void ResetDiscardsIncompleteRemainder()
    {
        var assembler = Started();
        assembler.Append(new byte[200], Timestamp);
        assembler.Reset();
        assembler.StartSession(Guid.NewGuid());
        Assert.Empty(assembler.Append(new byte[440], Timestamp));
        Assert.Equal(440, assembler.RemainderBytes);
    }

    [Fact]
    public void NewSessionResetsSequenceAndSampleIndex()
    {
        var assembler = Started();
        assembler.Append(new byte[640], Timestamp);
        var secondSession = Guid.NewGuid();
        assembler.StartSession(secondSession);
        var frame = Assert.Single(assembler.Append(new byte[640], Timestamp));
        Assert.Equal(secondSession, frame.SessionId);
        Assert.Equal(1, frame.Sequence);
        Assert.Equal(0, frame.StartSampleIndex);
    }

    [Fact]
    public void FrameOwnsImmutablePayloadCopy()
    {
        var input = Enumerable.Repeat((byte)7, 640).ToArray();
        var frame = Assert.Single(Started().Append(input, Timestamp));
        input[0] = 99;
        Assert.Equal(7, frame.Payload[0]);
    }

    private static AudioFrameAssembler Started()
    {
        var assembler = new AudioFrameAssembler();
        assembler.StartSession(Guid.NewGuid());
        return assembler;
    }
}
