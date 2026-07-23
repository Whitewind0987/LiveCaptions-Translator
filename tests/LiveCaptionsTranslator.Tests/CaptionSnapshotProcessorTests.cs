using LiveCaptionsTranslator.captioning;
using Xunit;

#pragma warning disable xUnit1051 // Managed integration uses an immediate synchronous fake source.

namespace LiveCaptionsTranslator.Tests;

public sealed class CaptionSnapshotProcessorTests
{
    [Fact]
    public void ChangedSnapshotUpdatesManagedCaptionState()
    {
        var processor = new CaptionSnapshotProcessor();

        var result = processor.Tick(1, "hello world.", 0, 2, 10, 10);

        Assert.True(result.SessionReset);
        Assert.True(result.HasSnapshot);
        Assert.Equal("hello world.", result.OriginalCaption);
        Assert.Equal("hello world.", result.DisplayOriginalCaption);
        Assert.Equal("hello world.", result.OverlayOriginalCaption);
        Assert.Equal("hello world.", result.TranslationTextToEnqueue);
    }

    [Fact]
    public void UnchangedSnapshotAdvancesIdleProgressionAndEventuallyEnqueues()
    {
        var processor = new CaptionSnapshotProcessor();
        processor.Tick(1, "unfinished caption", 0, 1, 100, 2);

        var firstUnchanged = processor.Tick(1, "unfinished caption", 0, 1, 100, 2);
        var secondUnchanged = processor.Tick(1, "unfinished caption", 0, 1, 100, 2);

        Assert.Equal(1, firstUnchanged.IdleCount);
        Assert.Null(firstUnchanged.TranslationTextToEnqueue);
        Assert.Equal(2, secondUnchanged.IdleCount);
        Assert.Equal("unfinished caption", secondUnchanged.TranslationTextToEnqueue);
    }

    [Fact]
    public void ResetClearsIdleAndSyncProgression()
    {
        var processor = new CaptionSnapshotProcessor();
        processor.Tick(1, "long unfinished caption", 0, 1, 100, 100);
        processor.Tick(1, "long unfinished caption", 0, 1, 100, 100);

        var reset = processor.Tick(2, null, 0, 1, 100, 100);

        Assert.True(reset.SessionReset);
        Assert.False(reset.HasSnapshot);
        Assert.Equal(0, reset.IdleCount);
        Assert.Equal(0, reset.SyncCount);
    }

    [Fact]
    public void NewSessionDoesNotReusePreviousOriginalCaption()
    {
        var processor = new CaptionSnapshotProcessor();
        processor.Tick(1, "same caption", 0, 1, 100, 2);
        processor.Tick(1, "same caption", 0, 1, 100, 2);

        var newSession = processor.Tick(2, "same caption", 0, 1, 100, 2);

        Assert.True(newSession.SessionReset);
        Assert.Equal(0, newSession.IdleCount);
        Assert.Null(newSession.TranslationTextToEnqueue);
    }

    [Theory]
    [InlineData("A . B test", "AB test")]
    [InlineData("你好 ， 世界。", "你好，世界。")]
    public void PunctuationPreprocessingMatchesLegacyRules(string raw, string expected)
    {
        Assert.Equal(expected, CaptionSnapshotProcessor.Preprocess(raw));
    }

    [Fact]
    public void SentenceExtractionPreservesShortSentenceExtension()
    {
        var processor = new CaptionSnapshotProcessor();

        var result = processor.Tick(1, "This is the previous sentence. short.", 0, 1, 10, 10);

        Assert.Equal("This is the previous sentence. short.", result.OriginalCaption);
    }

    [Fact]
    public void MissingSnapshotDoesNotEnqueueTranslation()
    {
        var processor = new CaptionSnapshotProcessor();

        var result = processor.Tick(0, null, 0, 1, 1, 1);

        Assert.False(result.HasSnapshot);
        Assert.Null(result.TranslationTextToEnqueue);
    }

    [Fact]
    public async Task ObsoleteSessionSnapshotIsNotProcessedAfterAcceptedReset()
    {
        var source = new FakeCaptionSource();
        await using var host = new CaptionSourceHost(source);
        var processor = new CaptionSnapshotProcessor();
        await host.StartAsync();
        source.Emit(CaptionEventFactory.Text(sequence: 2, text: "old session"));
        var oldState = host.ReadLatestState();
        Assert.True(processor.Tick(
            oldState.SessionGeneration, oldState.Snapshot?.Text, 0, 1, 10, 10).HasSnapshot);

        source.Emit(CaptionEventFactory.Reset(CaptionEventFactory.SessionB));
        source.Emit(CaptionEventFactory.Text(
            sessionId: CaptionEventFactory.SessionA,
            sequence: 3,
            revision: 2,
            text: "obsolete"));
        var resetState = host.ReadLatestState();
        var result = processor.Tick(
            resetState.SessionGeneration, resetState.Snapshot?.Text, 0, 1, 10, 10);

        Assert.True(result.SessionReset);
        Assert.False(result.HasSnapshot);
        Assert.Null(result.TranslationTextToEnqueue);
    }
}
