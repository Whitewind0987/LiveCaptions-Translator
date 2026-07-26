using LiveCaptionsTranslator.captioning;
using LiveCaptionsTranslator.captioning.local;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class LocalAsrCaptionSourceTests
{
    [Fact]
    public async Task ResetIsForwardedExactlyOnceAndPartialIsSuppressed()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);

        await source.StartAsync(TestContext.Current.CancellationToken);
        var reset = Reset();
        pipeline.Emit(reset);
        pipeline.Emit(Text(CaptionEventKind.Partial, 2, text: "draft"));

        Assert.Same(reset, Assert.Single(received));
    }

    [Fact]
    public async Task TextWithoutAcceptedResetIsNotForwarded()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);
        await source.StartAsync(TestContext.Current.CancellationToken);

        pipeline.Emit(Text(CaptionEventKind.Committed, 2));

        Assert.Empty(received);
    }

    [Fact]
    public async Task DuplicateAndStaleResetAreNotForwarded()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);
        await source.StartAsync(TestContext.Current.CancellationToken);
        var reset = Reset();

        pipeline.Emit(reset);
        pipeline.Emit(reset);
        pipeline.Emit(Text(CaptionEventKind.Committed, 2));
        pipeline.Emit(Reset(sequence: 1));

        Assert.Equal(
            [CaptionEventKind.Reset, CaptionEventKind.Final],
            received.Select(value => value.Kind));
        Assert.Same(reset, received[0]);
    }

    [Fact]
    public async Task CaptionSourceHostAcceptsForwardedResetThenStableCaption()
    {
        var pipeline = new FakePipeline();
        var source = CreateSource(pipeline);
        await using var host = new CaptionSourceHost(source);

        var result = await host.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        pipeline.Emit(Text(CaptionEventKind.Committed, 2, text: "stable"));

        Assert.True(result.Success);
        Assert.Equal(CaptionEventFactory.SessionA, host.ActiveSessionId);
        Assert.Equal(2, host.LastAcceptedSequence);
        Assert.Equal(1, host.CurrentSegmentId);
        Assert.Equal("stable", host.LatestSnapshot?.Text);
        Assert.Null(host.LastGateRejectionReason);
    }

    [Fact]
    public async Task CaptionSourceHostAcceptsTwoConsecutiveCommittedSegments()
    {
        var pipeline = new FakePipeline();
        var source = CreateSource(pipeline);
        await using var host = new CaptionSourceHost(source);
        await host.StartAsync(TestContext.Current.CancellationToken);

        pipeline.Emit(Reset());
        pipeline.Emit(Text(CaptionEventKind.Committed, 2, segment: 1, text: "one"));
        pipeline.Emit(Text(CaptionEventKind.Final, 3, segment: 1, text: "one"));
        pipeline.Emit(Text(CaptionEventKind.Committed, 4, segment: 2, text: "two"));

        Assert.Equal(2, host.CurrentSegmentId);
        Assert.Equal(4, host.LastAcceptedSequence);
        Assert.Equal("two", host.LatestSnapshot?.Text);
        Assert.Null(host.LastGateRejectionReason);
    }

    [Fact]
    public async Task CaptionSourceHostAcceptsThreeConsecutiveCommittedSegments()
    {
        var pipeline = new FakePipeline();
        var source = CreateSource(pipeline);
        await using var host = new CaptionSourceHost(source);
        await host.StartAsync(TestContext.Current.CancellationToken);

        pipeline.Emit(Reset());
        pipeline.Emit(Text(CaptionEventKind.Committed, 2, segment: 1, text: "one"));
        pipeline.Emit(Text(CaptionEventKind.Final, 3, segment: 1, text: "one"));
        pipeline.Emit(Text(CaptionEventKind.Committed, 4, segment: 2, text: "two"));
        pipeline.Emit(Text(CaptionEventKind.Final, 5, segment: 2, text: "two"));
        pipeline.Emit(Text(CaptionEventKind.Committed, 6, segment: 3, text: "three"));

        Assert.Equal(3, host.CurrentSegmentId);
        Assert.Equal(6, host.LastAcceptedSequence);
        Assert.Equal(6, host.HighestObservedSequence);
        Assert.Equal("three", host.LatestSnapshot?.Text);
        Assert.Null(host.LastGateRejectionReason);
    }

    [Fact]
    public async Task CommittedIsNormalizedToFinalWithAllFieldsPreserved()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        var emittedAt = new DateTimeOffset(2026, 7, 26, 8, 9, 10, TimeSpan.Zero);
        var committed = new CaptionEvent(
            CaptionEvent.CurrentSchemaVersion,
            CaptionEventFactory.SessionA,
            2,
            1,
            1,
            CaptionEventKind.Committed,
            "stable",
            120,
            960,
            emittedAt);

        pipeline.Emit(committed);

        Assert.Equal(
            [CaptionEventKind.Reset, CaptionEventKind.Final],
            received.Select(value => value.Kind));
        var normalized = received[1];
        Assert.NotSame(committed, normalized);
        Assert.Equal(committed.SchemaVersion, normalized.SchemaVersion);
        Assert.Equal(committed.SessionId, normalized.SessionId);
        Assert.Equal(committed.Sequence, normalized.Sequence);
        Assert.Equal(committed.SegmentId, normalized.SegmentId);
        Assert.Equal(committed.Revision, normalized.Revision);
        Assert.Equal(CaptionEventKind.Final, normalized.Kind);
        Assert.Equal(committed.Text, normalized.Text);
        Assert.Equal(committed.AudioStartMilliseconds, normalized.AudioStartMilliseconds);
        Assert.Equal(committed.AudioEndMilliseconds, normalized.AudioEndMilliseconds);
        Assert.Equal(committed.EmittedAtUtc, normalized.EmittedAtUtc);
    }

    [Fact]
    public async Task MatchingCommittedAndFinalPublishOnce()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());

        pipeline.Emit(Text(CaptionEventKind.Committed, 2, text: "stable"));
        pipeline.Emit(Text(CaptionEventKind.Final, 3, text: "stable"));

        Assert.Equal(
            [CaptionEventKind.Reset, CaptionEventKind.Final],
            received.Select(value => value.Kind));
    }

    [Fact]
    public async Task FinalOnlyPublishesStableCaptionWithMetadata()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        var final = Text(CaptionEventKind.Final, 2, text: "final only");

        pipeline.Emit(final);

        Assert.Same(final, received[1]);
        Assert.Equal([CaptionEventKind.Reset, CaptionEventKind.Final],
            received.Select(value => value.Kind));
    }

    [Fact]
    public async Task StaleRevisionIsIgnoredAndCurrentRevisionCanPublish()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        pipeline.Emit(Text(CaptionEventKind.Partial, 2, revision: 1, text: "one"));
        pipeline.Emit(Text(CaptionEventKind.Partial, 3, revision: 2, text: "two"));

        pipeline.Emit(Text(CaptionEventKind.Committed, 4, revision: 1, text: "one"));
        pipeline.Emit(Text(CaptionEventKind.Committed, 5, revision: 2, text: "two"));

        var caption = Assert.Single(received, value => value.Kind != CaptionEventKind.Reset);
        Assert.Equal(2, caption.Revision);
        Assert.Equal("two", caption.Text);
    }

    [Fact]
    public async Task IdenticalTextInDifferentSegmentsPublishesTwice()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());

        pipeline.Emit(Text(CaptionEventKind.Committed, 2, segment: 1, text: "same"));
        pipeline.Emit(Text(CaptionEventKind.Final, 3, segment: 1, text: "same"));
        pipeline.Emit(Text(CaptionEventKind.Committed, 4, segment: 2, text: "same"));

        Assert.Equal(3, received.Count);
        Assert.Equal(
            [1L, 2L],
            received.Where(value => value.Kind != CaptionEventKind.Reset)
                .Select(value => value.SegmentId));
    }

    [Fact]
    public async Task ResetClearsPerSegmentStableDeduplication()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        pipeline.Emit(Text(CaptionEventKind.Committed, 2, text: "first"));
        pipeline.Emit(Reset(sequence: 3));

        pipeline.Emit(Text(CaptionEventKind.Committed, 4, text: "after reset"));

        Assert.Equal(4, received.Count);
        Assert.Equal(
            ["first", "after reset"],
            received.Where(value => value.Kind != CaptionEventKind.Reset)
                .Select(value => value.Text));
        Assert.Equal(2, received.Count(value => value.Kind == CaptionEventKind.Reset));
    }

    [Fact]
    public async Task RepeatedAndConcurrentStartCreateOnePipelineAndSubscription()
    {
        var pipeline = new FakePipeline { StartGate = NewSignal() };
        var factoryCalls = 0;
        await using var source = new LocalAsrCaptionSource(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return pipeline;
        });

        var starts = Enumerable.Range(0, 8)
            .Select(_ => source.StartAsync(TestContext.Current.CancellationToken))
            .ToArray();
        await pipeline.StartEntered.Task;
        pipeline.StartGate.SetResult();
        var results = await Task.WhenAll(starts);
        var repeated = await source.StartAsync(TestContext.Current.CancellationToken);

        Assert.All(results.Append(repeated), result => Assert.True(result.Success));
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, pipeline.SubscriptionAdds);
        Assert.Equal(1, pipeline.StartCount);
    }

    [Fact]
    public async Task StopBeforeStartAndRepeatedStopAreSafe()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);

        await source.StopAsync(TestContext.Current.CancellationToken);
        await source.StartAsync(TestContext.Current.CancellationToken);
        await source.StopAsync(TestContext.Current.CancellationToken);
        await source.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, pipeline.StopCount);
        Assert.Equal(CaptionSourceState.Stopped, source.State);
    }

    [Fact]
    public async Task StopDetachesDisposesAndRejectsLaterEvents()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var count = 0;
        source.CaptionEventReceived += (_, _) => count++;
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        var staleHandler = pipeline.CurrentHandler;

        await source.StopAsync(TestContext.Current.CancellationToken);
        staleHandler?.Invoke(pipeline, Text(CaptionEventKind.Committed, 2));

        Assert.Equal(1, count);
        Assert.Equal(1, pipeline.SubscriptionRemoves);
        Assert.Equal(1, pipeline.DisposeCount);
    }

    [Fact]
    public async Task NormalStopPublishesStableCaptionFromPipelineDrain()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        pipeline.OnStop = () =>
            pipeline.Emit(Text(CaptionEventKind.Committed, 2, text: "drained"));

        await source.StopAsync(TestContext.Current.CancellationToken);

        var caption = Assert.Single(received, value => value.Kind != CaptionEventKind.Reset);
        Assert.Equal("drained", caption.Text);
        Assert.Equal(CaptionSourceState.Stopped, source.State);
    }

    [Fact]
    public async Task RestartCreatesFreshRunAndRejectsOldRunEvents()
    {
        var first = new FakePipeline { SessionId = CaptionEventFactory.SessionA };
        var second = new FakePipeline { SessionId = CaptionEventFactory.SessionB };
        var pipelines = new Queue<ILocalAsrPipeline>([first, second]);
        await using var source = new LocalAsrCaptionSource(() => pipelines.Dequeue());
        var received = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, value) => received.Add(value);

        await source.StartAsync(TestContext.Current.CancellationToken);
        first.Emit(Reset(CaptionEventFactory.SessionA));
        var oldHandler = first.CurrentHandler;
        await source.StopAsync(TestContext.Current.CancellationToken);
        await source.StartAsync(TestContext.Current.CancellationToken);
        second.Emit(Reset(CaptionEventFactory.SessionB));
        oldHandler?.Invoke(first, Text(CaptionEventKind.Committed, 2,
            session: CaptionEventFactory.SessionA, text: "old"));
        second.Emit(Text(CaptionEventKind.Committed, 2,
            session: CaptionEventFactory.SessionB, text: "new"));

        Assert.Equal(
            [
                (CaptionEventKind.Reset, CaptionEventFactory.SessionA),
                (CaptionEventKind.Reset, CaptionEventFactory.SessionB),
                (CaptionEventKind.Final, CaptionEventFactory.SessionB)
            ],
            received.Select(value => (value.Kind, value.SessionId)));
        var caption = received[^1];
        Assert.Equal("new", caption.Text);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.StartCount);
    }

    [Fact]
    public async Task FailedStartCleansUpAndLaterStartSucceeds()
    {
        var failed = new FakePipeline { StartFailure = new InvalidOperationException("start failed") };
        var successful = new FakePipeline { SessionId = CaptionEventFactory.SessionB };
        var pipelines = new Queue<ILocalAsrPipeline>([failed, successful]);
        await using var source = new LocalAsrCaptionSource(() => pipelines.Dequeue());

        var first = await source.StartAsync(TestContext.Current.CancellationToken);
        var second = await source.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(first.Success);
        Assert.Equal(CaptionSourceState.Faulted, first.State);
        Assert.Contains("start failed", first.FailureReason);
        Assert.Equal(1, failed.StopCount);
        Assert.Equal(1, failed.DisposeCount);
        Assert.True(second.Success);
        Assert.Equal(CaptionEventFactory.SessionB, second.SessionId);
    }

    [Fact]
    public async Task CallerCancellationDuringStartupCleansUpWithoutHanging()
    {
        var pipeline = new FakePipeline { StartGate = NewSignal() };
        await using var source = CreateSource(pipeline);
        using var cancellation = new CancellationTokenSource();
        var start = source.StartAsync(cancellation.Token);
        await pipeline.StartEntered.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, pipeline.DisposeCount);
        Assert.Equal(CaptionSourceState.Stopped, source.State);
    }

    [Fact]
    public async Task StopDuringStartupCancelsAndCleansUpWithoutHanging()
    {
        var pipeline = new FakePipeline { StartGate = NewSignal() };
        await using var source = CreateSource(pipeline);
        var start = source.StartAsync(TestContext.Current.CancellationToken);
        await pipeline.StartEntered.Task;

        await source.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, pipeline.DisposeCount);
        Assert.Equal(CaptionSourceState.Stopped, source.State);
    }

    [Fact]
    public async Task ReentrantStopSuppressesLaterSubscriberButAllowsDrainCaption()
    {
        var pipeline = new FakePipeline { StopGate = NewSignal() };
        await using var source = CreateSource(pipeline);
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        var stopRequested = 0;
        var subscriberB = new List<string>();
        source.CaptionEventReceived += (_, _) =>
        {
            if (Interlocked.Exchange(ref stopRequested, 1) == 0)
                source.StopAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        };
        source.CaptionEventReceived += (_, value) => subscriberB.Add(value.Text);

        await Task.Run(
                () => pipeline.Emit(Text(CaptionEventKind.Final, 2, text: "in flight")),
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await pipeline.StopEntered.Task;

        Assert.Equal(CaptionSourceState.Stopping, source.State);
        Assert.Empty(subscriberB);
        pipeline.Emit(Text(
            CaptionEventKind.Committed, 3, segment: 2, text: "drain"));
        Assert.Equal(["drain"], subscriberB);

        pipeline.StopGate.SetResult();
        await source.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        pipeline.Emit(Text(
            CaptionEventKind.Final, 4, segment: 2, text: "after stop"));

        Assert.Equal(CaptionSourceState.Stopped, source.State);
        Assert.Equal(["drain"], subscriberB);
        Assert.Equal(1, pipeline.DisposeCount);
    }

    [Fact]
    public async Task ExternalStopWaitsForCaptionDeliveryWithoutHoldingLifecycleLock()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        pipeline.Emit(Text(CaptionEventKind.Committed, 2, text: "first"));
        pipeline.Emit(Reset(sequence: 3));
        var entered = NewSignal();
        var release = NewSignal();
        var subscriberB = new List<CaptionEvent>();
        source.CaptionEventReceived += (_, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        source.CaptionEventReceived += (_, value) => subscriberB.Add(value);
        var publication = Task.Run(
            () => pipeline.Emit(Text(CaptionEventKind.Committed, 4, text: "second")),
            TestContext.Current.CancellationToken);
        await entered.Task;

        Assert.Empty(subscriberB);
        var stop = Task.Run(
            () => source.StopAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await pipeline.StopEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var stopWasIncompleteDuringPublication = !stop.IsCompleted;
        var stateDuringPublication = source.State;
        release.SetResult();
        await Task.WhenAll(publication, stop).WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.True(stopWasIncompleteDuringPublication);
        Assert.Equal(CaptionSourceState.Stopping, stateDuringPublication);
        var delivered = Assert.Single(subscriberB);
        Assert.Equal(CaptionEventKind.Final, delivered.Kind);
        Assert.Equal("second", delivered.Text);
        pipeline.Emit(Text(CaptionEventKind.Final, 5, text: "second"));
        Assert.Single(subscriberB);
        Assert.Equal(CaptionSourceState.Stopped, source.State);
    }

    [Fact]
    public async Task SubscriberExceptionsAreIsolated()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        var observed = 0;
        source.CaptionEventReceived += (_, _) => throw new InvalidOperationException("subscriber");
        source.CaptionEventReceived += (_, _) => observed++;

        pipeline.Emit(Text(CaptionEventKind.Committed, 2));

        Assert.Equal(1, observed);
        Assert.Equal(CaptionSourceState.Running, source.State);
    }

    [Fact]
    public async Task StopRecordedBeforeStartingPublicationSuppressesStaleStatus()
    {
        var pipeline = new FakePipeline { StartGate = NewSignal() };
        var dispatcher = new ControlledStatusDispatcher();
        await using var source = new LocalAsrCaptionSource(
            () => pipeline, dispatcher.Schedule);
        var observed = new List<CaptionSourceState>();
        source.StatusChanged += (_, status) => observed.Add(status.State);
        var start = source.StartAsync(TestContext.Current.CancellationToken);
        await pipeline.StartEntered.Task;

        await source.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        dispatcher.DrainAll();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal([CaptionSourceState.Stopped], observed);
        Assert.Equal(CaptionSourceState.Stopped, source.State);
    }

    [Fact]
    public async Task StopRecordedBeforeRunningPublicationPreventsRunningAfterStopping()
    {
        var pipeline = new FakePipeline
        {
            StartGate = NewSignal(),
            StopGate = NewSignal()
        };
        var dispatcher = new ControlledStatusDispatcher();
        await using var source = new LocalAsrCaptionSource(
            () => pipeline, dispatcher.Schedule);
        var observed = new List<CaptionSourceState>();
        source.StatusChanged += (_, status) => observed.Add(status.State);
        var start = source.StartAsync(TestContext.Current.CancellationToken);
        await pipeline.StartEntered.Task;
        dispatcher.DrainAll();
        Assert.Equal([CaptionSourceState.Starting], observed);
        dispatcher.BlockNextSchedule();
        pipeline.StartGate.SetResult();
        await dispatcher.ScheduleBlocked.Task;
        Assert.Equal(CaptionSourceState.Running, source.State);
        Assert.False(start.IsCompleted);

        var stop = source.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CaptionSourceState.Stopping, source.State);
        dispatcher.ReleaseBlockedSchedule();
        await pipeline.StopEntered.Task;
        dispatcher.DrainAll();

        Assert.Equal(
            [CaptionSourceState.Starting, CaptionSourceState.Stopping], observed);
        Assert.DoesNotContain(CaptionSourceState.Running, observed);
        pipeline.StopGate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await stop.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        dispatcher.DrainAll();
        Assert.Equal(
            [
                CaptionSourceState.Starting,
                CaptionSourceState.Stopping,
                CaptionSourceState.Stopped
            ], observed);
    }

    [Fact]
    public async Task StartingStatusSubscriberCanStopReentrantlyWithoutDeadlock()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        var observed = new List<CaptionSourceState>();
        source.StatusChanged += (_, status) =>
        {
            observed.Add(status.State);
            if (status.State == CaptionSourceState.Starting)
                source.StopAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        };

        var start = source.StartAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await source.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(CaptionSourceState.Starting, observed[0]);
        Assert.DoesNotContain(
            observed.Zip(observed.Skip(1)),
            pair => pair.First == CaptionSourceState.Stopping &&
                    pair.Second is CaptionSourceState.Starting or CaptionSourceState.Running);
        Assert.Equal(CaptionSourceState.Stopped, source.State);
    }

    [Fact]
    public async Task StatusSubscriberCanStopWithoutLifecycleLockDeadlock()
    {
        var pipeline = new FakePipeline();
        await using var source = CreateSource(pipeline);
        source.StatusChanged += (_, status) =>
        {
            if (status.State == CaptionSourceState.Running)
                source.StopAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        };

        var start = source.StartAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await source.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CaptionSourceState.Stopped, source.State);
    }

    [Fact]
    public async Task DisposalIsIdempotentAndRejectsOldEvents()
    {
        var pipeline = new FakePipeline();
        var source = CreateSource(pipeline);
        var count = 0;
        source.CaptionEventReceived += (_, _) => count++;
        await source.StartAsync(TestContext.Current.CancellationToken);
        pipeline.Emit(Reset());
        var oldHandler = pipeline.CurrentHandler;

        await source.DisposeAsync();
        await source.DisposeAsync();
        oldHandler?.Invoke(pipeline, Text(CaptionEventKind.Committed, 2));

        Assert.Equal(1, count);
        Assert.Equal(1, pipeline.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            source.StartAsync(TestContext.Current.CancellationToken));
    }

    private static LocalAsrCaptionSource CreateSource(FakePipeline pipeline) =>
        new(() => pipeline);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CaptionEvent Reset(Guid? session = null, long sequence = 1) =>
        CaptionEventFactory.Reset(session, sequence);

    private static CaptionEvent Text(
        CaptionEventKind kind,
        long sequence,
        Guid? session = null,
        long segment = 1,
        long revision = 1,
        string text = "caption") =>
        CaptionEventFactory.Text(kind, session, sequence, segment, revision, text);

    private sealed class ControlledStatusDispatcher
    {
        private readonly Queue<Action> pending = [];
        private readonly object gate = new();
        private TaskCompletionSource? blockedScheduleRelease;

        internal TaskCompletionSource ScheduleBlocked { get; private set; } = NewSignal();

        internal void Schedule(Action action)
        {
            lock (pending) pending.Enqueue(action);
            Task? wait = null;
            lock (gate)
            {
                if (blockedScheduleRelease != null)
                {
                    ScheduleBlocked.TrySetResult();
                    wait = blockedScheduleRelease.Task;
                }
            }
            wait?.GetAwaiter().GetResult();
        }

        internal void BlockNextSchedule()
        {
            lock (gate)
            {
                ScheduleBlocked = NewSignal();
                blockedScheduleRelease = NewSignal();
            }
        }

        internal void ReleaseBlockedSchedule()
        {
            TaskCompletionSource? release;
            lock (gate)
            {
                release = blockedScheduleRelease;
                blockedScheduleRelease = null;
            }
            release?.TrySetResult();
        }

        internal void DrainAll()
        {
            while (true)
            {
                Action? action;
                lock (pending)
                    action = pending.Count == 0 ? null : pending.Dequeue();
                if (action == null) return;
                action();
            }
        }
    }

    private sealed class FakePipeline : ILocalAsrPipeline
    {
        private EventHandler<CaptionEvent>? captionEventReceived;

        internal Guid? SessionId { get; set; } = CaptionEventFactory.SessionA;
        internal TaskCompletionSource StartEntered { get; } = NewSignal();
        internal TaskCompletionSource StopEntered { get; } = NewSignal();
        internal TaskCompletionSource? StartGate { get; set; }
        internal TaskCompletionSource? StopGate { get; set; }
        internal Exception? StartFailure { get; set; }
        internal Action? OnStop { get; set; }
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal int SubscriptionAdds { get; private set; }
        internal int SubscriptionRemoves { get; private set; }
        internal EventHandler<CaptionEvent>? CurrentHandler => captionEventReceived;

        Guid? ILocalAsrPipeline.SessionId => SessionId;

        public event EventHandler<CaptionEvent>? CaptionEventReceived
        {
            add
            {
                SubscriptionAdds++;
                captionEventReceived += value;
            }
            remove
            {
                SubscriptionRemoves++;
                captionEventReceived -= value;
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            StartEntered.TrySetResult();
            if (StartFailure != null) throw StartFailure;
            if (StartGate != null)
                await StartGate.Task.WaitAsync(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            StopEntered.TrySetResult();
            OnStop?.Invoke();
            if (StopGate != null)
                await StopGate.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        internal void Emit(CaptionEvent captionEvent) =>
            captionEventReceived?.Invoke(this, captionEvent);
    }
}
