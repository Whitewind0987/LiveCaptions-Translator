using System.Collections.Concurrent;

using LiveCaptionsTranslator.captioning;
using LiveCaptionsTranslator.captioning.windows;
using Xunit;

#pragma warning disable xUnit1051 // Lifecycle tests use controlled fake cancellation and bounded wait helpers.

namespace LiveCaptionsTranslator.Tests;

public sealed class WindowsLiveCaptionsSourceTests
{
    [Fact]
    public async Task InitialStateIsStoppedAndSourceIdentityIsStable()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        await using var source = CreateSource(runtime);

        Assert.Equal(CaptionSourceState.Stopped, source.State);
        Assert.Null(source.FailureReason);
        Assert.False(string.IsNullOrWhiteSpace(source.SourceId));
        Assert.Equal(source.SourceId, source.SourceId);
        Assert.Equal(0, runtime.ReadCount);
    }

    [Fact]
    public async Task SuccessfulStartEmitsOneResetAndDuplicateStartUsesOnePoller()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        await using var source = CreateSource(runtime);
        var events = new ConcurrentQueue<CaptionEvent>();
        var statuses = new ConcurrentQueue<CaptionSourceState>();
        source.CaptionEventReceived += (_, captionEvent) => events.Enqueue(captionEvent);
        source.StatusChanged += (_, status) => statuses.Enqueue(status.State);

        var result = await source.StartAsync();
        await AsyncTest.WaitUntilAsync(() => runtime.ReadCount == 1);
        var duplicateResult = await source.StartAsync();

        Assert.True(result.Success);
        Assert.Equal(result.SessionId, duplicateResult.SessionId);
        Assert.Equal(CaptionSourceState.Running, source.State);
        Assert.Equal(1, runtime.InitializeCount);
        Assert.Equal(1, runtime.ReadCount);
        var reset = Assert.Single(events);
        Assert.Equal(CaptionEventKind.Reset, reset.Kind);
        Assert.Equal(1, reset.Sequence);
        Assert.Equal(result.SessionId, reset.SessionId);
        Assert.Equal(new[] { CaptionSourceState.Starting, CaptionSourceState.Running }, statuses);
    }

    [Fact]
    public async Task CancelledStartEmitsNoEventOrPoller()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        await using var source = CreateSource(runtime);
        var eventCount = 0;
        source.CaptionEventReceived += (_, _) => eventCount++;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.StartAsync(cancellation.Token));

        Assert.Equal(0, eventCount);
        Assert.Equal(0, runtime.InitializeCount);
        Assert.Equal(0, runtime.ReadCount);
        Assert.Equal(CaptionSourceState.Stopped, source.State);
    }

    [Fact]
    public async Task SubscriberExceptionsDoNotBreakStartupOrLaterSnapshots()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        await using var source = CreateSource(runtime);
        var received = new ConcurrentQueue<CaptionEvent>();
        source.CaptionEventReceived += (_, _) => throw new InvalidOperationException("event handler");
        source.CaptionEventReceived += (_, captionEvent) => received.Enqueue(captionEvent);
        source.StatusChanged += (_, _) => throw new InvalidOperationException("status handler");

        await source.StartAsync();
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Snapshot("first"));
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Snapshot("second"));
        await AsyncTest.WaitUntilAsync(() => received.Count == 3);

        Assert.Equal(CaptionSourceState.Running, source.State);
        Assert.Equal(new[] { CaptionEventKind.Reset, CaptionEventKind.Partial, CaptionEventKind.Partial },
            received.Select(item => item.Kind));
    }

    [Theory]
    [InlineData(0, CaptionSourceState.Unavailable)]
    [InlineData(1, CaptionSourceState.Faulted)]
    public async Task StartFailureMapsTypedStateAndCleansUp(
        int categoryValue,
        CaptionSourceState expectedState)
    {
        var category = (LiveCaptionsRuntimeFailureCategory)categoryValue;
        var runtime = new FakeLiveCaptionsRuntime();
        runtime.EnqueueInitialization(LiveCaptionsRuntimeInitializationResult.Failed(category, "init failed"));
        await using var source = CreateSource(runtime);
        var eventCount = 0;
        source.CaptionEventReceived += (_, _) => eventCount++;

        var result = await source.StartAsync();

        Assert.False(result.Success);
        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedState, source.State);
        Assert.Contains("init failed", source.FailureReason);
        Assert.Equal(0, eventCount);
        Assert.Equal(0, runtime.ReadCount);
        Assert.Equal(1, runtime.ShutdownCount);
    }

    [Fact]
    public async Task ChangedSnapshotsEmitValidOrderedPartialEventsOnly()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        await using var source = CreateSource(runtime);
        var events = new ConcurrentQueue<CaptionEvent>();
        source.CaptionEventReceived += (_, captionEvent) => events.Enqueue(captionEvent);

        await source.StartAsync();
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.NoText());
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Snapshot("   "));
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Snapshot("caption one"));
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Snapshot("caption one"));
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Snapshot("caption two"));
        await AsyncTest.WaitUntilAsync(() => events.Count == 3);

        var acceptedEvents = events.ToArray();
        Assert.Equal(CaptionEventKind.Reset, acceptedEvents[0].Kind);
        Assert.Equal(new long[] { 1, 2, 3 }, acceptedEvents.Select(item => item.Sequence));
        Assert.Equal(new long[] { 0, 1, 2 }, acceptedEvents.Select(item => item.Revision));
        Assert.Equal(new long[] { 0, 1, 1 }, acceptedEvents.Select(item => item.SegmentId));
        Assert.All(acceptedEvents, item => Assert.Equal(TimeSpan.Zero, item.EmittedAtUtc.Offset));
        Assert.All(acceptedEvents.Skip(1), item => Assert.Equal(CaptionEventKind.Partial, item.Kind));

        var gate = new CaptionEventGate();
        Assert.All(acceptedEvents, item => Assert.True(gate.TryAccept(item, out _)));
    }

    [Fact]
    public async Task SuccessfulRestartUsesOneDelayAndStartsNewSessionBeforeText()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        runtime.EnqueueInitialization(LiveCaptionsRuntimeInitializationResult.Started());
        runtime.EnqueueInitialization(LiveCaptionsRuntimeInitializationResult.Started());
        var delay = new FakeCaptionSourceDelay();
        await using var source = new WindowsLiveCaptionsSource(runtime, delay);
        var events = new ConcurrentQueue<CaptionEvent>();
        var statuses = new ConcurrentQueue<CaptionSourceState>();
        source.CaptionEventReceived += (_, captionEvent) => events.Enqueue(captionEvent);
        source.StatusChanged += (_, status) => statuses.Enqueue(status.State);

        await source.StartAsync();
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Snapshot("same text"));
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.WindowLost("lost"));
        await AsyncTest.WaitUntilAsync(() => statuses.Contains(CaptionSourceState.Restarting));
        await AsyncTest.WaitUntilAsync(() => statuses.Count(item => item == CaptionSourceState.Running) == 2);
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Snapshot("same text"));
        await AsyncTest.WaitUntilAsync(() => events.Count == 4);

        var emitted = events.ToArray();
        Assert.Equal(1, delay.RestartDelayCount);
        Assert.Equal(2, runtime.InitializeCount);
        Assert.Equal(CaptionEventKind.Reset, emitted[0].Kind);
        Assert.Equal(CaptionEventKind.Partial, emitted[1].Kind);
        Assert.Equal(CaptionEventKind.Reset, emitted[2].Kind);
        Assert.Equal(CaptionEventKind.Partial, emitted[3].Kind);
        Assert.NotEqual(emitted[0].SessionId, emitted[2].SessionId);
        Assert.Equal(1, emitted[2].Sequence);
        Assert.Equal(1, emitted[3].Revision);
        Assert.Equal("same text", emitted[3].Text);
    }

    [Theory]
    [InlineData(0, CaptionSourceState.Unavailable)]
    [InlineData(1, CaptionSourceState.Faulted)]
    public async Task FailedRestartMapsTypedFailureWithoutNewSession(
        int categoryValue,
        CaptionSourceState expectedState)
    {
        var category = (LiveCaptionsRuntimeFailureCategory)categoryValue;
        var runtime = new FakeLiveCaptionsRuntime();
        runtime.EnqueueInitialization(LiveCaptionsRuntimeInitializationResult.Started());
        runtime.EnqueueInitialization(LiveCaptionsRuntimeInitializationResult.Failed(category, "restart failed"));
        await using var source = CreateSource(runtime);
        var events = new ConcurrentQueue<CaptionEvent>();
        source.CaptionEventReceived += (_, captionEvent) => events.Enqueue(captionEvent);

        await source.StartAsync();
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.WindowLost("lost"));
        await AsyncTest.WaitUntilAsync(() => source.State == expectedState);

        Assert.Equal(2, runtime.InitializeCount);
        Assert.Single(events);
        Assert.Equal(CaptionEventKind.Reset, events.Single().Kind);
        Assert.Contains("restart failed", source.FailureReason);
    }

    [Fact]
    public async Task StopDuringRestartDelayPreventsInitializationAndLaterEvents()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        var delay = new FakeCaptionSourceDelay { BlockRestart = true };
        await using var source = new WindowsLiveCaptionsSource(runtime, delay);
        var events = new ConcurrentQueue<CaptionEvent>();
        source.CaptionEventReceived += (_, captionEvent) => events.Enqueue(captionEvent);

        await source.StartAsync();
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.WindowLost("lost"));
        await delay.RestartEntered;
        await source.StopAsync();
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Snapshot("too late"));
        await Task.Yield();

        Assert.Equal(CaptionSourceState.Stopped, source.State);
        Assert.Equal(1, runtime.InitializeCount);
        Assert.Single(events);
        Assert.Equal(1, delay.RestartDelayCount);
    }

    [Fact]
    public async Task StopCancelsPollingPublishesStatesAndCleansUpOnce()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        await using var source = CreateSource(runtime);
        var events = new ConcurrentQueue<CaptionEvent>();
        var statuses = new ConcurrentQueue<CaptionSourceState>();
        source.CaptionEventReceived += (_, captionEvent) => events.Enqueue(captionEvent);
        source.StatusChanged += (_, status) => statuses.Enqueue(status.State);
        await source.StartAsync();
        await AsyncTest.WaitUntilAsync(() => runtime.ReadCount == 1);

        await Task.WhenAll(source.StopAsync(), source.StopAsync());
        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Snapshot("after stop"));
        await Task.Yield();

        Assert.Equal(CaptionSourceState.Stopped, source.State);
        Assert.Contains(CaptionSourceState.Stopping, statuses);
        Assert.Equal(CaptionSourceState.Stopped, statuses.Last());
        Assert.Equal(1, runtime.ShutdownCount);
        Assert.Single(events);
    }

    [Fact]
    public async Task CleanupFailureIsRetainedWithoutEscaping()
    {
        var runtime = new FakeLiveCaptionsRuntime
        {
            CleanupResult = LiveCaptionsRuntimeCleanupResult.Failed("cleanup failed")
        };
        await using var source = CreateSource(runtime);
        await source.StartAsync();

        await source.StopAsync();

        Assert.Equal(CaptionSourceState.Stopped, source.State);
        Assert.Contains("cleanup failed", source.FailureReason);
    }

    [Fact]
    public async Task FailedInitializationRetainsCleanupFailure()
    {
        var runtime = new FakeLiveCaptionsRuntime
        {
            CleanupResult = LiveCaptionsRuntimeCleanupResult.Failed("cleanup also failed")
        };
        runtime.EnqueueInitialization(LiveCaptionsRuntimeInitializationResult.Failed(
            LiveCaptionsRuntimeFailureCategory.Faulted,
            "initialization failed"));
        await using var source = CreateSource(runtime);

        var result = await source.StartAsync();

        Assert.False(result.Success);
        Assert.Contains("initialization failed", result.FailureReason);
        Assert.Contains("cleanup also failed", result.FailureReason);
    }

    [Fact]
    public async Task RuntimeReadFailureFaultsSourceAndStopsPolling()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        await using var source = CreateSource(runtime);
        await source.StartAsync();

        runtime.EnqueueSnapshot(LiveCaptionsSnapshotReadResult.Faulted("read failed"));
        await AsyncTest.WaitUntilAsync(() => source.State == CaptionSourceState.Faulted);
        var readCount = runtime.ReadCount;
        await Task.Yield();

        Assert.Equal(readCount, runtime.ReadCount);
        Assert.Contains("read failed", source.FailureReason);
    }

    [Fact]
    public async Task StopWhileAlreadyStoppedDoesNotInvokeRuntimeCleanup()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        await using var source = CreateSource(runtime);

        await source.StopAsync();

        Assert.Equal(CaptionSourceState.Stopped, source.State);
        Assert.Equal(0, runtime.ShutdownCount);
    }

    [Fact]
    public async Task StartAfterStopCreatesCompletelyNewSession()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        await using var source = CreateSource(runtime);

        var first = await source.StartAsync();
        await source.StopAsync();
        runtime.IsWindowAvailable = true;
        var second = await source.StartAsync();

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(2, runtime.InitializeCount);
    }

    [Fact]
    public async Task RepeatedDisposalIsSafeAndStopsSource()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        var source = CreateSource(runtime);
        await source.StartAsync();

        await source.DisposeAsync();
        await source.DisposeAsync();

        Assert.Equal(CaptionSourceState.Stopped, source.State);
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task WindowCapabilityShowsHidesAndReportsVisibility()
    {
        var runtime = new FakeLiveCaptionsRuntime();
        await using var source = CreateSource(runtime);
        await source.StartAsync();

        var shown = await source.ShowAsync();
        var hidden = await source.HideAsync();

        Assert.True(shown.Success);
        Assert.True(hidden.Success);
        Assert.Equal(1, runtime.ShowCount);
        Assert.Equal(1, runtime.HideCount);
        Assert.False(source.IsWindowVisible);
    }

    [Fact]
    public async Task WindowCapabilityReturnsTypedFailuresWithoutThrowing()
    {
        var runtime = new FakeLiveCaptionsRuntime
        {
            ShowResult = CaptionWindowControlResult.Failed(
                CaptionWindowControlFailure.Faulted, "show failed")
        };
        await using var source = CreateSource(runtime);

        var unavailable = await source.ShowAsync();
        await source.StartAsync();
        var faulted = await source.ShowAsync();

        Assert.False(unavailable.Success);
        Assert.Equal(CaptionWindowControlFailure.Unavailable, unavailable.Failure);
        Assert.False(faulted.Success);
        Assert.Equal(CaptionWindowControlFailure.Faulted, faulted.Failure);
    }

    [Fact]
    public void PublicWindowCapabilityDoesNotExposeAutomationTypes()
    {
        var exposedTypes = typeof(INativeCaptionWindowControl)
            .GetMembers()
            .SelectMany(member => member switch
            {
                System.Reflection.PropertyInfo property => new[] { property.PropertyType },
                System.Reflection.MethodInfo method =>
                    method.GetParameters().Select(parameter => parameter.ParameterType)
                        .Append(method.ReturnType),
                _ => []
            });

        Assert.DoesNotContain(exposedTypes,
            type => type.FullName?.Contains("AutomationElement", StringComparison.Ordinal) == true);
    }

    private static WindowsLiveCaptionsSource CreateSource(FakeLiveCaptionsRuntime runtime) =>
        new(runtime, new FakeCaptionSourceDelay());
}
