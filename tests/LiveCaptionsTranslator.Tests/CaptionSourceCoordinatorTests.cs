using System.Collections.Concurrent;
using System.Reflection;

using LiveCaptionsTranslator.captioning;
using Xunit;

#pragma warning disable xUnit1051 // Controlled fakes use explicit cancellation gates where relevant.

namespace LiveCaptionsTranslator.Tests;

public sealed class CaptionSourceCoordinatorTests
{
    [Fact]
    public async Task DefaultStartIsIdempotentAndForwardsAcceptedState()
    {
        var tracker = new SourceTracker();
        var windows = new ControlledCaptionSource("windows", tracker);
        var localFactoryCalls = 0;
        var hostCount = 0;
        await using var coordinator = CreateCoordinator(
            () => windows,
            () =>
            {
                localFactoryCalls++;
                return new ControlledCaptionSource("local", tracker);
            },
            () => hostCount++);
        var statuses = new List<CaptionSourceStatus>();
        var snapshots = new List<AcceptedCaptionSnapshot?>();
        coordinator.StatusChanged += (_, status) => statuses.Add(status);
        coordinator.SnapshotChanged += (_, snapshot) => snapshots.Add(snapshot);

        var first = await coordinator.StartAsync();
        var second = await coordinator.StartAsync();
        windows.EmitText("accepted caption");

        Assert.True(first.Success);
        Assert.Same(first, second);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, CaptionSourceCoordinator.DefaultSource);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, coordinator.CurrentSource);
        Assert.Equal(1, windows.StartCount);
        Assert.Equal(1, hostCount);
        Assert.Equal(0, localFactoryCalls);
        Assert.Contains(statuses, value => value.State == CaptionSourceState.Running);
        Assert.Contains(snapshots, value => value?.Text == "accepted caption");
        Assert.Equal("accepted caption", coordinator.ReadLatestState().Snapshot?.Text);
    }

    [Fact]
    public async Task SelectingActiveSourceDoesNotRestartOrDuplicateSubscriptions()
    {
        var source = new ControlledCaptionSource("windows", new SourceTracker());
        await using var coordinator = new CaptionSourceCoordinator(() => source);
        await coordinator.StartAsync();
        source.EmitText("retained");
        var stateBefore = coordinator.ReadLatestState();

        await coordinator.SelectAsync(CaptionSourceKind.WindowsLiveCaptions);

        Assert.Equal(1, source.StartCount);
        Assert.Equal(0, source.StopCount);
        Assert.Equal(0, source.DisposeCount);
        Assert.Equal(1, source.CaptionSubscriberAdds);
        Assert.Equal(1, source.StatusSubscriberAdds);
        Assert.Same(stateBefore.Snapshot, coordinator.ReadLatestState().Snapshot);
    }

    [Fact]
    public async Task SelectingFaultedActiveSourceReplacesItWithFreshSource()
    {
        var tracker = new SourceTracker();
        var faulted = new ControlledCaptionSource("local-faulted", tracker);
        var replacement = new ControlledCaptionSource("local-replacement", tracker);
        var sources = new Queue<ControlledCaptionSource>([faulted, replacement]);
        await using var coordinator = new CaptionSourceCoordinator(
            () => new ControlledCaptionSource("windows", tracker),
            () => sources.Dequeue());
        await coordinator.SelectAsync(CaptionSourceKind.LocalAsr);
        faulted.FailRuntime("worker exited");

        var result = await coordinator.SelectAsync(CaptionSourceKind.LocalAsr);

        Assert.True(result.Success);
        Assert.Equal(CaptionSourceState.Running, coordinator.State);
        Assert.Equal(1, faulted.StopCount);
        Assert.Equal(1, faulted.DisposeCount);
        Assert.Equal(1, replacement.StartCount);
        Assert.Equal(1, tracker.MaxActive);
    }

    [Fact]
    public async Task SwitchStopsAndDisposesOldSourceBeforeFreshSourceStarts()
    {
        var tracker = new SourceTracker();
        var windows = new ControlledCaptionSource("windows", tracker);
        var local = new ControlledCaptionSource("local", tracker);
        var hosts = 0;
        await using var coordinator = CreateCoordinator(
            () => windows,
            () => local,
            () => hosts++);
        await coordinator.StartAsync();
        windows.EmitText("old");

        var result = await coordinator.SelectAsync(CaptionSourceKind.LocalAsr);
        local.EmitText("new");

        Assert.True(result.Success);
        Assert.Equal(CaptionSourceKind.LocalAsr, coordinator.CurrentSource);
        Assert.Equal(2, hosts);
        Assert.Equal(1, windows.StopCount);
        Assert.Equal(1, windows.DisposeCount);
        Assert.Equal(1, local.StartCount);
        Assert.Equal(1, tracker.MaxActive);
        AssertOrdered(tracker.Events, "windows:stop", "windows:dispose", "local:start");
        Assert.NotEqual(windows.SessionId, local.SessionId);
        Assert.Equal("new", coordinator.ReadLatestState().Snapshot?.Text);
    }

    [Fact]
    public async Task ReplacementRejectsOldCallbacksAndRepeatedSwitchesDoNotAccumulateHandlers()
    {
        var tracker = new SourceTracker();
        var windows1 = new ControlledCaptionSource("windows-1", tracker)
        {
            EmitStaleCallbacksDuringStop = true
        };
        var windows2 = new ControlledCaptionSource("windows-2", tracker);
        var local = new ControlledCaptionSource("local", tracker)
        {
            EmitStaleCallbacksDuringStop = true
        };
        var windowsSources = new Queue<ControlledCaptionSource>([windows1, windows2]);
        await using var coordinator = new CaptionSourceCoordinator(
            () => windowsSources.Dequeue(),
            () => local);
        var forwardedText = new List<string>();
        var forwardedStatusIds = new List<string>();
        coordinator.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot != null)
                forwardedText.Add(snapshot.Text);
        };
        coordinator.StatusChanged += (_, status) => forwardedStatusIds.Add(status.SourceId);
        await coordinator.StartAsync();
        windows1.EmitText("windows-current");

        await coordinator.SelectAsync(CaptionSourceKind.LocalAsr);
        local.EmitText("local-current");
        await coordinator.SelectAsync(CaptionSourceKind.WindowsLiveCaptions);
        windows2.EmitText("windows-new");
        windows1.EmitText("late-windows");
        local.EmitText("late-local");

        Assert.DoesNotContain("stale-windows-1", forwardedText);
        Assert.DoesNotContain("stale-local", forwardedText);
        Assert.DoesNotContain("late-windows", forwardedText);
        Assert.DoesNotContain("late-local", forwardedText);
        Assert.Equal("windows-new", coordinator.ReadLatestState().Snapshot?.Text);
        Assert.Equal(1, windows1.CaptionSubscriberAdds);
        Assert.Equal(1, windows1.CaptionSubscriberRemoves);
        Assert.Equal(1, local.CaptionSubscriberAdds);
        Assert.Equal(1, local.CaptionSubscriberRemoves);
        Assert.DoesNotContain("stale-status-windows-1", forwardedStatusIds);
        Assert.DoesNotContain("stale-status-local", forwardedStatusIds);
    }

    [Fact]
    public async Task CreationFailureDoesNotRestoreOldSourceAndRetryCanSucceed()
    {
        var tracker = new SourceTracker();
        var windows = new ControlledCaptionSource("windows", tracker);
        var local = new ControlledCaptionSource("local", tracker);
        var attempts = 0;
        await using var coordinator = new CaptionSourceCoordinator(
            () => windows,
            () => ++attempts == 1
                ? throw new InvalidOperationException("factory failed")
                : local);
        await coordinator.StartAsync();

        var failed = await coordinator.SelectAsync(CaptionSourceKind.LocalAsr);
        var retried = await coordinator.SelectAsync(CaptionSourceKind.LocalAsr);

        Assert.False(failed.Success);
        Assert.Equal(CaptionSourceState.Faulted, failed.State);
        Assert.Contains("factory failed", failed.FailureReason);
        Assert.True(retried.Success);
        Assert.Equal(1, windows.StartCount);
        Assert.Equal(1, windows.StopCount);
        Assert.Equal(1, windows.DisposeCount);
        Assert.Equal(1, local.StartCount);
        Assert.Equal(1, tracker.MaxActive);
    }

    [Fact]
    public async Task HostCreationFailureStopsAndDisposesCreatedSource()
    {
        var source = new ControlledCaptionSource("local", new SourceTracker());
        await using var coordinator = new CaptionSourceCoordinator(
            () => new ControlledCaptionSource("windows", new SourceTracker()),
            () => source,
            _ => throw new InvalidOperationException("host factory failed"));

        var result = await coordinator.SelectAsync(CaptionSourceKind.LocalAsr);

        Assert.False(result.Success);
        Assert.Equal(CaptionSourceState.Faulted, result.State);
        Assert.Contains("host factory failed", result.FailureReason);
        Assert.Equal(1, source.StopCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.Null(coordinator.CurrentSource);
    }

    [Fact]
    public async Task FailedStartCleansTargetAndLaterRetrySucceeds()
    {
        var tracker = new SourceTracker();
        var failedLocal = new ControlledCaptionSource("local-failed", tracker)
        {
            StartResult = CaptionSourceStartResult.Failed(
                CaptionSourceState.Unavailable,
                "model unavailable")
        };
        var successfulLocal = new ControlledCaptionSource("local-success", tracker);
        var locals = new Queue<ControlledCaptionSource>([failedLocal, successfulLocal]);
        await using var coordinator = new CaptionSourceCoordinator(
            () => new ControlledCaptionSource("windows", tracker),
            () => locals.Dequeue());

        var failed = await coordinator.SelectAsync(CaptionSourceKind.LocalAsr);
        var failedState = coordinator.State;
        var retried = await coordinator.SelectAsync(CaptionSourceKind.LocalAsr);

        Assert.False(failed.Success);
        Assert.Equal(CaptionSourceState.Unavailable, failed.State);
        Assert.Equal(CaptionSourceState.Unavailable, failedState);
        Assert.Equal(1, failedLocal.StopCount);
        Assert.Equal(1, failedLocal.DisposeCount);
        Assert.True(retried.Success);
        Assert.Equal(CaptionSourceKind.LocalAsr, coordinator.CurrentSource);
        Assert.Equal(CaptionSourceState.Running, coordinator.State);
    }

    [Fact]
    public async Task CancellationDuringTargetStartCleansTargetAndAllowsLaterSelection()
    {
        var tracker = new SourceTracker();
        var windows1 = new ControlledCaptionSource("windows-1", tracker);
        var windows2 = new ControlledCaptionSource("windows-2", tracker);
        var windowsSources = new Queue<ControlledCaptionSource>([windows1, windows2]);
        var local = new ControlledCaptionSource("local", tracker) { BlockStart = true };
        await using var coordinator = new CaptionSourceCoordinator(
            () => windowsSources.Dequeue(),
            () => local);
        await coordinator.StartAsync();
        using var cancellation = new CancellationTokenSource();

        var selection = coordinator.SelectAsync(CaptionSourceKind.LocalAsr, cancellation.Token);
        await local.StartEntered;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.Equal(1, local.StopCount);
        Assert.Equal(1, local.DisposeCount);
        Assert.Null(coordinator.CurrentSource);
        Assert.Equal(CaptionSourceState.Stopped, coordinator.State);
        var retry = await coordinator.SelectAsync(CaptionSourceKind.WindowsLiveCaptions);
        Assert.True(retry.Success);
        Assert.Equal(1, tracker.MaxActive);
    }

    [Fact]
    public async Task ConcurrentSelectionsAreSerializedWithoutLifetimeOverlap()
    {
        var tracker = new SourceTracker();
        var windows1 = new ControlledCaptionSource("windows-1", tracker);
        var windows2 = new ControlledCaptionSource("windows-2", tracker);
        var windowsSources = new Queue<ControlledCaptionSource>([windows1, windows2]);
        var local = new ControlledCaptionSource("local", tracker) { BlockStart = true };
        await using var coordinator = new CaptionSourceCoordinator(
            () => windowsSources.Dequeue(),
            () => local);
        await coordinator.StartAsync();

        var selectLocal = coordinator.SelectAsync(CaptionSourceKind.LocalAsr);
        await local.StartEntered;
        var selectWindows = coordinator.SelectAsync(CaptionSourceKind.WindowsLiveCaptions);
        Assert.False(selectWindows.IsCompleted);
        local.ReleaseStart();
        await selectLocal;
        await selectWindows;

        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, coordinator.CurrentSource);
        Assert.Equal(1, tracker.MaxActive);
        AssertOrdered(tracker.Events, "windows-1:dispose", "local:start", "local:dispose", "windows-2:start");
    }

    [Fact]
    public async Task ExternalStopDuringSelectionCancelsAndJoinsCleanup()
    {
        var tracker = new SourceTracker();
        var target = new ControlledCaptionSource("local", tracker) { BlockStart = true };
        await using var coordinator = new CaptionSourceCoordinator(
            () => new ControlledCaptionSource("windows", tracker),
            () => target);
        var selection = coordinator.SelectAsync(CaptionSourceKind.LocalAsr);
        await target.StartEntered;

        var stop = coordinator.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        await stop;

        Assert.Null(coordinator.CurrentSource);
        Assert.Equal(CaptionSourceState.Stopped, coordinator.State);
        Assert.Equal(1, target.StopCount);
        Assert.Equal(1, target.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousCancellationStatusCannotReplaceTrackedShutdown(bool dispose)
    {
        var source = new ControlledCaptionSource("local", new SourceTracker())
        {
            BlockStart = true,
            EmitStatusDuringCancellation = true
        };
        var callbackReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CaptionSourceCoordinator? coordinator = null;
        source.StatusChanged += (_, status) =>
        {
            if (status.FailureReason != "synchronous cancellation callback")
                return;

            if (dispose)
                coordinator!.DisposeAsync().AsTask().GetAwaiter().GetResult();
            else
                coordinator!.StopAsync().GetAwaiter().GetResult();
            callbackReturned.TrySetResult();
        };
        coordinator = new CaptionSourceCoordinator(
            () => new ControlledCaptionSource("windows", new SourceTracker()),
            () => source);
        var selection = coordinator.SelectAsync(CaptionSourceKind.LocalAsr);
        await source.StartEntered;

        var primaryShutdown = dispose
            ? coordinator.DisposeAsync().AsTask()
            : coordinator.StopAsync();
        var joinedShutdown = dispose
            ? coordinator.DisposeAsync().AsTask()
            : coordinator.StopAsync();

        Assert.Same(primaryShutdown, joinedShutdown);
        await callbackReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        await primaryShutdown;
        await joinedShutdown;

        Assert.Equal(1, source.StopCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.Null(coordinator.CurrentSource);
        if (dispose)
            await coordinator.DisposeAsync();
        else
            await coordinator.StopAsync();
        Assert.Equal(1, source.StopCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task UnpublishedCanceledTargetIsFullyCleanedAndCanBeRetried()
    {
        var tracker = new SourceTracker();
        var local = new ControlledCaptionSource("local", tracker);
        var windows = new ControlledCaptionSource("windows", tracker);
        using var cancellation = new CancellationTokenSource();
        CaptionSourceHost? unpublishedHost = null;
        var hostFactoryCalls = 0;
        await using var coordinator = new CaptionSourceCoordinator(
            () => windows,
            () => local,
            source =>
            {
                var host = new CaptionSourceHost(source);
                if (++hostFactoryCalls == 1)
                {
                    unpublishedHost = host;
                    cancellation.Cancel();
                }
                return host;
            });
        var forwardedSnapshots = 0;
        coordinator.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot != null)
                forwardedSnapshots++;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.SelectAsync(CaptionSourceKind.LocalAsr, cancellation.Token));

        Assert.NotNull(unpublishedHost);
        Assert.Equal(1, local.StopCount);
        Assert.Equal(1, local.DisposeCount);
        Assert.Equal(1, local.CaptionSubscriberRemoves);
        Assert.Equal(1, local.StatusSubscriberRemoves);
        Assert.Equal(0, GetHostSubscriberCount(unpublishedHost!, "SnapshotChanged"));
        Assert.Equal(0, GetHostSubscriberCount(unpublishedHost!, "StatusChanged"));
        Assert.Null(coordinator.CurrentSource);
        local.EmitText("after cleanup");
        Assert.Equal(0, forwardedSnapshots);

        var retry = await coordinator.SelectAsync(CaptionSourceKind.WindowsLiveCaptions);
        Assert.True(retry.Success);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, coordinator.CurrentSource);
    }

    [Fact]
    public async Task PublishedTargetDetachedByDisposeIsCleanedOnlyByDisposal()
    {
        var target = new ControlledCaptionSource("local", new SourceTracker())
        {
            BlockStart = true
        };
        var coordinator = new CaptionSourceCoordinator(
            () => new ControlledCaptionSource("windows", new SourceTracker()),
            () => target);
        var selection = coordinator.SelectAsync(CaptionSourceKind.LocalAsr);
        await target.StartEntered;

        var disposal = coordinator.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        await disposal;
        await coordinator.DisposeAsync();

        Assert.Equal(1, target.StopCount);
        Assert.Equal(1, target.DisposeCount);
        Assert.Equal(1, target.CaptionSubscriberRemoves);
        Assert.Equal(1, target.StatusSubscriberRemoves);
        Assert.Null(coordinator.CurrentSource);
    }

    [Fact]
    public async Task UnsuccessfulPublishedTargetTakenByStopIsNotDoubleCleaned()
    {
        var target = new ControlledCaptionSource("local", new SourceTracker())
        {
            StartResult = CaptionSourceStartResult.Failed(
                CaptionSourceState.Unavailable,
                "deterministic start failure")
        };
        var coordinator = new CaptionSourceCoordinator(
            () => new ControlledCaptionSource("windows", new SourceTracker()),
            () => target);
        var callbackReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State != CaptionSourceState.Unavailable)
                return;

            coordinator.StopAsync().GetAwaiter().GetResult();
            callbackReturned.TrySetResult();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.SelectAsync(CaptionSourceKind.LocalAsr));
        await callbackReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.StopAsync();

        Assert.Equal(1, target.StopCount);
        Assert.Equal(1, target.DisposeCount);
        Assert.Null(coordinator.CurrentSource);
        Assert.Equal(CaptionSourceState.Stopped, coordinator.State);
    }

    [Fact]
    public async Task DisposePreservesPendingStopFailureAndFaultedState()
    {
        var source = new ControlledCaptionSource("windows", new SourceTracker())
        {
            BlockStop = true
        };
        var coordinator = new CaptionSourceCoordinator(() => source);
        await coordinator.StartAsync();

        var stop = coordinator.StopAsync();
        await source.StopEntered;
        var disposal = coordinator.DisposeAsync().AsTask();
        var repeatedDisposal = coordinator.DisposeAsync().AsTask();
        Assert.Same(disposal, repeatedDisposal);
        source.FailStop(new InvalidOperationException("deterministic stop failure"));

        var stopFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => stop);
        var disposeFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => disposal);
        var repeatedFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repeatedDisposal);
        var finalRepeatedDisposal = coordinator.DisposeAsync().AsTask();
        Assert.Same(disposal, finalRepeatedDisposal);
        var finalRepeatedFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalRepeatedDisposal);

        Assert.Contains("deterministic stop failure", stopFailure.Message);
        Assert.Contains("deterministic stop failure", disposeFailure.Message);
        Assert.Contains("deterministic stop failure", repeatedFailure.Message);
        Assert.Contains("deterministic stop failure", finalRepeatedFailure.Message);
        Assert.Equal(CaptionSourceState.Faulted, coordinator.State);
        Assert.Contains("deterministic stop failure", coordinator.FailureReason);
        Assert.Equal(1, source.StopCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.Null(coordinator.CurrentSource);
        Assert.Equal(CaptionSourceState.Faulted, coordinator.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackTriggeredShutdownDoesNotDeadlock(bool dispose)
    {
        var source = new ControlledCaptionSource("windows", new SourceTracker());
        var coordinator = new CaptionSourceCoordinator(() => source);
        var callbackReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State != CaptionSourceState.Running)
                return;

            if (dispose)
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            else
                coordinator.StopAsync().GetAwaiter().GetResult();
            callbackReturned.TrySetResult();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.StartAsync());
        await callbackReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (dispose)
            await coordinator.DisposeAsync();
        else
            await coordinator.StopAsync();

        Assert.Equal(1, source.StopCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.Null(coordinator.CurrentSource);
    }

    [Fact]
    public async Task StopAndDisposeAreSafeIdempotentAndSuppressLaterCallbacks()
    {
        var source = new ControlledCaptionSource("windows", new SourceTracker());
        var coordinator = new CaptionSourceCoordinator(() => source);
        var forwarded = 0;
        coordinator.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot != null)
                forwarded++;
        };

        await coordinator.StopAsync();
        await coordinator.StartAsync();
        source.EmitText("before stop");
        await coordinator.StopAsync();
        await coordinator.StopAsync();
        source.EmitText("after stop");
        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();
        source.EmitText("after dispose");

        Assert.Equal(1, forwarded);
        Assert.Equal(1, source.StopCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(0, source.CaptionSubscriberCount);
        Assert.Equal(0, source.StatusSubscriberCount);
        Assert.Null(coordinator.CurrentSource);
    }

    [Fact]
    public async Task MissingLocalFactoryFailsExplicitlyWithoutFallback()
    {
        var windows = new ControlledCaptionSource("windows", new SourceTracker());
        await using var coordinator = new CaptionSourceCoordinator(() => windows);

        var result = await coordinator.SelectAsync(CaptionSourceKind.LocalAsr);

        Assert.False(result.Success);
        Assert.Equal(CaptionSourceState.Unavailable, result.State);
        Assert.Contains("No production factory", result.FailureReason);
        Assert.Equal(0, windows.StartCount);
        Assert.Null(coordinator.CurrentSource);
    }

    private static CaptionSourceCoordinator CreateCoordinator(
        Func<ICaptionSource> windowsFactory,
        Func<ICaptionSource> localFactory,
        Action hostCreated) =>
        new(
            windowsFactory,
            localFactory,
            source =>
            {
                hostCreated();
                return new CaptionSourceHost(source);
            });

    private static int GetHostSubscriberCount(CaptionSourceHost host, string eventName)
    {
        var field = typeof(CaptionSourceHost).GetField(
            eventName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (field?.GetValue(host) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static void AssertOrdered(
        IReadOnlyCollection<string> events,
        params string[] expected)
    {
        var actual = events.ToList();
        var previous = -1;
        foreach (var value in expected)
        {
            var index = actual.IndexOf(value);
            Assert.True(index > previous, $"'{value}' was not ordered after the previous event: {string.Join(", ", actual)}");
            previous = index;
        }
    }

    private sealed class SourceTracker
    {
        private readonly object sync = new();
        private readonly ConcurrentQueue<string> events = new();
        private int active;

        internal IReadOnlyCollection<string> Events => events.ToArray();
        internal int MaxActive { get; private set; }

        internal void Record(string value) => events.Enqueue(value);

        internal void Activate()
        {
            lock (sync)
            {
                active++;
                MaxActive = Math.Max(MaxActive, active);
            }
        }

        internal void Deactivate()
        {
            lock (sync)
                active--;
        }
    }

    private sealed class ControlledCaptionSource : ICaptionSource
    {
        private readonly SourceTracker tracker;
        private readonly TaskCompletionSource startEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource startRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource stopEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource stopRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler<CaptionEvent>? captionEventReceived;
        private EventHandler<CaptionSourceStatus>? statusChanged;
        private long sequence = 1;
        private bool active;

        internal ControlledCaptionSource(string sourceId, SourceTracker tracker)
        {
            SourceId = sourceId;
            this.tracker = tracker;
            SessionId = Guid.NewGuid();
        }

        internal Guid SessionId { get; }
        internal bool BlockStart { get; init; }
        internal bool BlockStop { get; init; }
        internal bool EmitStatusDuringCancellation { get; init; }
        internal bool EmitStaleCallbacksDuringStop { get; init; }
        internal CaptionSourceStartResult? StartResult { get; init; }
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal int CaptionSubscriberAdds { get; private set; }
        internal int CaptionSubscriberRemoves { get; private set; }
        internal int StatusSubscriberAdds { get; private set; }
        internal int StatusSubscriberRemoves { get; private set; }
        internal int CaptionSubscriberCount =>
            captionEventReceived?.GetInvocationList().Length ?? 0;
        internal int StatusSubscriberCount =>
            statusChanged?.GetInvocationList().Length ?? 0;
        internal Task StartEntered => startEntered.Task;
        internal Task StopEntered => stopEntered.Task;

        public string SourceId { get; }
        public CaptionSourceState State { get; private set; } = CaptionSourceState.Stopped;
        public string? FailureReason { get; private set; }

        public event EventHandler<CaptionEvent>? CaptionEventReceived
        {
            add
            {
                CaptionSubscriberAdds++;
                captionEventReceived += value;
            }
            remove
            {
                CaptionSubscriberRemoves++;
                captionEventReceived -= value;
            }
        }

        public event EventHandler<CaptionSourceStatus>? StatusChanged
        {
            add
            {
                StatusSubscriberAdds++;
                statusChanged += value;
            }
            remove
            {
                StatusSubscriberRemoves++;
                statusChanged -= value;
            }
        }

        public async Task<CaptionSourceStartResult> StartAsync(
            CancellationToken cancellationToken = default)
        {
            var startCancellation = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                if (EmitStatusDuringCancellation)
                {
                    State = CaptionSourceState.Restarting;
                    FailureReason = "synchronous cancellation callback";
                    PublishStatus();
                }
                startCancellation.TrySetCanceled(cancellationToken);
            });
            StartCount++;
            tracker.Record($"{SourceId}:start-entered");
            State = CaptionSourceState.Starting;
            PublishStatus();
            startEntered.TrySetResult();
            if (BlockStart)
                await await Task.WhenAny(startRelease.Task, startCancellation.Task);
            cancellationToken.ThrowIfCancellationRequested();

            var result = StartResult ?? CaptionSourceStartResult.Started(SessionId);
            if (!result.Success)
            {
                State = result.State;
                FailureReason = result.FailureReason;
                PublishStatus();
                return result;
            }

            Emit(CaptionEventFactory.Reset(SessionId));
            State = CaptionSourceState.Running;
            FailureReason = null;
            active = true;
            tracker.Activate();
            tracker.Record($"{SourceId}:start");
            PublishStatus();
            return result;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            tracker.Record($"{SourceId}:stop");
            stopEntered.TrySetResult();
            if (BlockStop)
                await stopRelease.Task;
            if (EmitStaleCallbacksDuringStop)
            {
                EmitText($"stale-{SourceId}");
                statusChanged?.Invoke(
                    this,
                    new CaptionSourceStatus(
                        $"stale-status-{SourceId}",
                        CaptionSourceState.Running,
                        null,
                        DateTimeOffset.UtcNow));
            }

            if (active)
            {
                active = false;
                tracker.Deactivate();
            }
            State = CaptionSourceState.Stopped;
            FailureReason = null;
            PublishStatus();
        }

        internal void ReleaseStart() => startRelease.TrySetResult();

        internal void FailStop(Exception failure) => stopRelease.TrySetException(failure);

        internal void FailRuntime(string reason)
        {
            State = CaptionSourceState.Faulted;
            FailureReason = reason;
            PublishStatus();
        }

        internal void EmitText(string text)
        {
            sequence++;
            Emit(CaptionEventFactory.Text(
                sessionId: SessionId,
                sequence: sequence,
                revision: sequence - 1,
                text: text));
        }

        private void Emit(CaptionEvent captionEvent) =>
            captionEventReceived?.Invoke(this, captionEvent);

        private void PublishStatus() => statusChanged?.Invoke(
            this,
            new CaptionSourceStatus(SourceId, State, FailureReason, DateTimeOffset.UtcNow));

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (active)
            {
                active = false;
                tracker.Deactivate();
            }
            tracker.Record($"{SourceId}:dispose");
            return ValueTask.CompletedTask;
        }
    }
}
