using LiveCaptionsTranslator.captioning;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class CaptionEventTests
{
    [Fact]
    public void ValidResetIsCreated()
    {
        var captionEvent = CaptionEventFactory.Reset();

        Assert.Equal(CaptionEventKind.Reset, captionEvent.Kind);
        Assert.Equal(string.Empty, captionEvent.Text);
        Assert.Equal(0, captionEvent.SegmentId);
        Assert.Equal(0, captionEvent.Revision);
    }

    [Theory]
    [InlineData(CaptionEventKind.Partial)]
    [InlineData(CaptionEventKind.Committed)]
    [InlineData(CaptionEventKind.Final)]
    public void ValidTextEventIsCreated(CaptionEventKind kind)
    {
        var captionEvent = CaptionEventFactory.Text(kind);

        Assert.Equal(kind, captionEvent.Kind);
        Assert.Equal("caption", captionEvent.Text);
        Assert.Equal(1, captionEvent.SegmentId);
        Assert.Equal(1, captionEvent.Revision);
    }

    [Fact]
    public void UnsupportedSchemaIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(schemaVersion: 2));
    }

    [Fact]
    public void EmptySessionIdentityIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(sessionId: Guid.Empty));
    }

    [Fact]
    public void InvalidSequenceIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(sequence: 0));
    }

    [Fact]
    public void ResetTextIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(
            kind: CaptionEventKind.Reset, segmentId: 0, revision: 0, text: "reset"));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void InvalidResetSegmentOrRevisionIsRejected(long segmentId, long revision)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            kind: CaptionEventKind.Reset,
            segmentId: segmentId,
            revision: revision,
            text: string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTextEventIsRejected(string text)
    {
        Assert.Throws<ArgumentException>(() => Create(text: text));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void InvalidTextEventSegmentOrRevisionIsRejected(long segmentId, long revision)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            segmentId: segmentId,
            revision: revision));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, -1)]
    public void NegativeAudioTimestampIsRejected(long audioStart, long audioEnd)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            audioStartMilliseconds: audioStart,
            audioEndMilliseconds: audioEnd));
    }

    [Fact]
    public void AudioEndEarlierThanStartIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(
            audioStartMilliseconds: 20,
            audioEndMilliseconds: 10));
    }

    [Fact]
    public void DefaultEmissionTimestampIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(emittedAtUtc: new DateTimeOffset()));
    }

    private static CaptionEvent Create(
        int schemaVersion = CaptionEvent.CurrentSchemaVersion,
        Guid? sessionId = null,
        long sequence = 1,
        long segmentId = 1,
        long revision = 1,
        CaptionEventKind kind = CaptionEventKind.Partial,
        string text = "caption",
        long? audioStartMilliseconds = null,
        long? audioEndMilliseconds = null,
        DateTimeOffset? emittedAtUtc = null) =>
        new(
            schemaVersion,
            sessionId ?? CaptionEventFactory.SessionA,
            sequence,
            segmentId,
            revision,
            kind,
            text,
            audioStartMilliseconds,
            audioEndMilliseconds,
            emittedAtUtc ?? CaptionEventFactory.EmittedAtUtc);
}
