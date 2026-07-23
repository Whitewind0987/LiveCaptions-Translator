using LiveCaptionsTranslator.captioning;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class CaptionTranslationRequestTests
{
    [Fact]
    public void CreatedFromCommittedEvent()
    {
        var captionEvent = CaptionEventFactory.Text(CaptionEventKind.Committed);

        var request = CaptionTranslationRequest.FromCaptionEvent(captionEvent);

        Assert.True(request.IsCommitted);
        Assert.False(request.IsFinal);
        AssertIdentityPreserved(captionEvent, request);
    }

    [Fact]
    public void CreatedFromFinalEvent()
    {
        var captionEvent = CaptionEventFactory.Text(CaptionEventKind.Final);

        var request = CaptionTranslationRequest.FromCaptionEvent(captionEvent);

        Assert.False(request.IsCommitted);
        Assert.True(request.IsFinal);
        AssertIdentityPreserved(captionEvent, request);
    }

    [Fact]
    public void PartialEventIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            CaptionTranslationRequest.FromCaptionEvent(CaptionEventFactory.Text()));
    }

    [Fact]
    public void ResetEventIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            CaptionTranslationRequest.FromCaptionEvent(CaptionEventFactory.Reset()));
    }

    private static void AssertIdentityPreserved(
        CaptionEvent captionEvent,
        CaptionTranslationRequest request)
    {
        Assert.Equal(captionEvent.SessionId, request.SessionId);
        Assert.Equal(captionEvent.Sequence, request.Sequence);
        Assert.Equal(captionEvent.SegmentId, request.SegmentId);
        Assert.Equal(captionEvent.Revision, request.Revision);
        Assert.Equal(captionEvent.Text, request.Text);
        Assert.Equal(captionEvent.Kind, request.OriginatingEventKind);
    }
}
