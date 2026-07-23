using LiveCaptionsTranslator.captioning;
using Xunit;

#pragma warning disable xUnit1051 // Host tests use synchronous fakes and complete immediately.

namespace LiveCaptionsTranslator.Tests;

public sealed class CaptionSourceHostTests
{
    [Fact]
    public async Task HostSubscribesBeforeStartAndAcceptsInitialReset()
    {
        var source = new FakeCaptionSource();
        await using var host = new CaptionSourceHost(source);

        var result = await host.StartAsync();

        Assert.True(result.Success);
        Assert.Equal(CaptionEventFactory.SessionA, host.ActiveSessionId);
        Assert.Equal(1, host.LastAcceptedSequence);
        Assert.Equal(1, host.HighestObservedSequence);
        Assert.Equal(1, host.SessionGeneration);
        Assert.Null(host.LatestSnapshot);
    }

    [Fact]
    public async Task AcceptedPartialUpdatesLatestSnapshotAndDiagnostics()
    {
        var source = new FakeCaptionSource();
        await using var host = new CaptionSourceHost(source);
        await host.StartAsync();

        source.Emit(CaptionEventFactory.Text(sequence: 2, text: "latest"));

        var snapshot = Assert.IsType<AcceptedCaptionSnapshot>(host.LatestSnapshot);
        Assert.Equal("latest", snapshot.Text);
        Assert.Equal(CaptionEventFactory.SessionA, snapshot.SessionId);
        Assert.Equal(2, host.LastAcceptedSequence);
        Assert.Equal(1, host.CurrentSegmentId);
        Assert.Equal(1, host.CurrentRevision);
    }

    [Fact]
    public async Task DuplicateAndStaleEventsDoNotOverwriteLatestSnapshot()
    {
        var source = new FakeCaptionSource();
        await using var host = new CaptionSourceHost(source);
        await host.StartAsync();
        source.Emit(CaptionEventFactory.Text(sequence: 3, text: "accepted"));

        source.Emit(CaptionEventFactory.Text(sequence: 3, revision: 2, text: "duplicate"));
        source.Emit(CaptionEventFactory.Text(sequence: 2, revision: 2, text: "stale"));

        Assert.Equal("accepted", host.LatestSnapshot?.Text);
        Assert.Equal(3, host.LastAcceptedSequence);
        Assert.False(string.IsNullOrWhiteSpace(host.LastGateRejectionReason));
    }

    [Fact]
    public async Task ForeignSessionEventDoesNotOverwriteSnapshot()
    {
        var source = new FakeCaptionSource();
        await using var host = new CaptionSourceHost(source);
        await host.StartAsync();
        source.Emit(CaptionEventFactory.Text(sequence: 2, text: "active"));

        source.Emit(CaptionEventFactory.Text(
            sessionId: CaptionEventFactory.SessionB,
            sequence: 100,
            text: "foreign"));

        Assert.Equal("active", host.LatestSnapshot?.Text);
        Assert.Equal(2, host.HighestObservedSequence);
    }

    [Fact]
    public async Task AcceptedNewSessionResetClearsSnapshotAndUpdatesGeneration()
    {
        var source = new FakeCaptionSource();
        await using var host = new CaptionSourceHost(source);
        await host.StartAsync();
        source.Emit(CaptionEventFactory.Text(sequence: 2));

        source.Emit(CaptionEventFactory.Reset(CaptionEventFactory.SessionB));

        Assert.Null(host.LatestSnapshot);
        Assert.Equal(CaptionEventFactory.SessionB, host.ActiveSessionId);
        Assert.Equal(1, host.LastAcceptedSequence);
        Assert.Equal(2, host.SessionGeneration);
    }

    [Fact]
    public async Task SourceStatusIsRetainedAndPublished()
    {
        var source = new FakeCaptionSource();
        await using var host = new CaptionSourceHost(source);
        CaptionSourceStatus? observed = null;
        host.StatusChanged += (_, status) => observed = status;
        await host.StartAsync();

        source.SetStatus(CaptionSourceState.Faulted, "source failed");

        Assert.Equal(CaptionSourceState.Faulted, host.State);
        Assert.Equal("source failed", host.FailureReason);
        Assert.Equal(CaptionSourceState.Faulted, observed?.State);
    }

    [Theory]
    [InlineData(CaptionSourceState.Restarting)]
    [InlineData(CaptionSourceState.Unavailable)]
    [InlineData(CaptionSourceState.Faulted)]
    public async Task InactiveStateInvalidatesSnapshotUntilNewSessionProducesText(
        CaptionSourceState inactiveState)
    {
        var source = new FakeCaptionSource();
        await using var host = new CaptionSourceHost(source);
        var processor = new CaptionSnapshotProcessor();
        await host.StartAsync();
        source.Emit(CaptionEventFactory.Text(sequence: 2, text: "old unfinished caption"));

        var runningState = host.ReadLatestState();
        Assert.Equal(CaptionSourceState.Running, runningState.State);
        Assert.True(processor.Tick(
            runningState.SessionGeneration,
            runningState.Snapshot?.Text,
            0,
            1,
            100,
            1).HasSnapshot);

        source.SetStatus(inactiveState, "source disrupted");
        source.Emit(CaptionEventFactory.Text(
            sequence: 3,
            revision: 2,
            text: "delayed old caption"));

        var inactiveSourceState = host.ReadLatestState();
        var inactiveResult = processor.Tick(
            inactiveSourceState.SessionGeneration,
            inactiveSourceState.Snapshot?.Text,
            0,
            1,
            100,
            1);

        Assert.Equal(inactiveState, inactiveSourceState.State);
        Assert.Null(inactiveSourceState.Snapshot);
        Assert.False(inactiveResult.HasSnapshot);
        Assert.Null(inactiveResult.DisplayOriginalCaption);
        Assert.Null(inactiveResult.OverlayOriginalCaption);
        Assert.Null(inactiveResult.TranslationTextToEnqueue);
        Assert.Equal(3, host.LastAcceptedSequence);

        source.Emit(CaptionEventFactory.Reset(CaptionEventFactory.SessionB));
        source.SetStatus(CaptionSourceState.Running);
        source.Emit(CaptionEventFactory.Text(
            sessionId: CaptionEventFactory.SessionB,
            sequence: 2,
            text: "new session caption"));

        var recoveredState = host.ReadLatestState();
        var recoveredResult = processor.Tick(
            recoveredState.SessionGeneration,
            recoveredState.Snapshot?.Text,
            0,
            1,
            100,
            1);

        Assert.Equal(CaptionSourceState.Running, recoveredState.State);
        Assert.Equal(2, recoveredState.SessionGeneration);
        Assert.Equal("new session caption", recoveredState.Snapshot?.Text);
        Assert.True(recoveredResult.SessionReset);
        Assert.True(recoveredResult.HasSnapshot);
    }

    [Fact]
    public async Task HostStartsAndStopsSourceExactlyOnce()
    {
        var source = new FakeCaptionSource();
        await using var host = new CaptionSourceHost(source);

        var first = await host.StartAsync();
        var second = await host.StartAsync();
        await host.StopAsync();
        await host.StopAsync();

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(1, source.StartCount);
        Assert.Equal(1, source.StopCount);
    }

    [Fact]
    public async Task SubscriberExceptionDoesNotCorruptLaterHostState()
    {
        var source = new FakeCaptionSource();
        await using var host = new CaptionSourceHost(source);
        host.SnapshotChanged += (_, _) => throw new InvalidOperationException("subscriber");
        await host.StartAsync();

        source.Emit(CaptionEventFactory.Text(sequence: 2, text: "first"));
        source.Emit(CaptionEventFactory.Text(sequence: 3, revision: 2, text: "second"));

        Assert.Equal("second", host.LatestSnapshot?.Text);
        Assert.Equal(3, host.LastAcceptedSequence);
    }
}
