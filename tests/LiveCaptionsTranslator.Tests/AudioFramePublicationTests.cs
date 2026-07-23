using LiveCaptionsTranslator.audio;
using Xunit;

#pragma warning disable xUnit1051

namespace LiveCaptionsTranslator.Tests;

public sealed class AudioFramePublicationTests
{
    [Fact]
    public async Task DedicatedCaptureThreadCanExitWhenSubscriberSynchronouslyStops()
    {
        var runtime = new DedicatedThreadAudioCaptureRuntime();
        await using var service = CreateService(runtime);
        await service.StartAsync(null);
        var subscriberCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriberThreadId = 0;
        var eventCount = 0;
        var laterHandlerCount = 0;
        service.FrameProduced += (_, _) =>
        {
            subscriberThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref eventCount);
            service.StopAsync().GetAwaiter().GetResult();
            subscriberCompleted.TrySetResult(null);
        };
        service.FrameProduced += (_, _) => Interlocked.Increment(ref laterHandlerCount);

        runtime.RequestData();
        await subscriberCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.CallbackReturned.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.ThreadExited.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(runtime.CallbackThreadId, subscriberThreadId);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(1, eventCount);
        Assert.Equal(0, laterHandlerCount);
        Assert.Equal(AudioCaptureState.Stopped, service.State);
    }

    [Fact]
    public async Task DataDuringRuntimeStartWaitsForRunningStatusAndManagedDispatcher()
    {
        var runtime = new DedicatedThreadAudioCaptureRuntime(emitDuringStart: true);
        await using var service = CreateService(runtime);
        var order = new List<string>();
        var orderLock = new object();
        var framePublished = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriberThreadId = 0;
        service.StatusChanged += (_, status) =>
        {
            lock (orderLock)
                order.Add(status.State.ToString());
        };
        service.FrameProduced += (_, _) =>
        {
            subscriberThreadId = Environment.CurrentManagedThreadId;
            lock (orderLock)
                order.Add("Frame");
            framePublished.TrySetResult(null);
        };

        await service.StartAsync(null);
        await framePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lock (orderLock)
            Assert.True(order.IndexOf("Running") < order.IndexOf("Frame"));
        Assert.NotEqual(runtime.CallbackThreadId, subscriberThreadId);
    }

    [Fact]
    public async Task QueuedNotificationsPreserveFrameSequenceOrder()
    {
        var runtime = new FakeAudioCaptureRuntime();
        await using var service = CreateService(runtime);
        var sequences = new List<long>();
        var completed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.FrameProduced += (_, frame) =>
        {
            sequences.Add(frame.Sequence);
            if (sequences.Count == 3)
                completed.TrySetResult(null);
        };
        await service.StartAsync(null);

        runtime.EmitData(AudioTestData.ConstantPcm16Frame());
        runtime.EmitData(AudioTestData.ConstantPcm16Frame());
        runtime.EmitData(AudioTestData.ConstantPcm16Frame());
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new long[] { 1, 2, 3 }, sequences);
    }

    [Fact]
    public async Task NotificationDispatcherEnforcesBoundedCapacity()
    {
        var runtime = new FakeAudioCaptureRuntime();
        var dispatchEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        await using var service = CreateService(runtime, beforeDispatch: () =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                dispatchEntered.TrySetResult(null);
                releaseDispatch.Task.GetAwaiter().GetResult();
            }
        });
        await service.StartAsync(null);
        runtime.EmitData(AudioTestData.ConstantPcm16Frame());
        await dispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var index = 0; index < service.FrameNotificationCapacity + 5; index++)
            runtime.EmitData(AudioTestData.ConstantPcm16Frame());

        Assert.Equal(service.FrameNotificationCapacity, service.FrameNotificationsQueued);
        Assert.Equal(service.FrameNotificationCapacity, service.MaximumFrameNotificationsQueued);
        Assert.Equal(5, service.FrameNotificationsDropped);
        releaseDispatch.TrySetResult(null);
    }

    [Fact]
    public async Task StopDiscardsQueuedNotificationsAndRemainingHandlers()
    {
        var runtime = new FakeAudioCaptureRuntime();
        await using var service = CreateService(runtime);
        await service.StartAsync(null);
        var firstEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventCount = 0;
        service.FrameProduced += (_, _) =>
        {
            Interlocked.Increment(ref eventCount);
            firstEntered.TrySetResult(null);
            releaseFirst.Task.GetAwaiter().GetResult();
        };

        runtime.EmitData(AudioTestData.ConstantPcm16Frame());
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.EmitData(AudioTestData.ConstantPcm16Frame());
        var stop = service.StopAsync();
        Assert.False(stop.IsCompleted);
        releaseFirst.TrySetResult(null);
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, eventCount);
        Assert.True(service.PublicationDispatchersCompleted);
    }

    [Fact]
    public async Task TerminalStatusCannotBeFollowedByQueuedFrameNotification()
    {
        var runtime = new FakeAudioCaptureRuntime();
        var dispatchEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService(runtime, beforeDispatch: () =>
        {
            dispatchEntered.TrySetResult(null);
            releaseDispatch.Task.GetAwaiter().GetResult();
        });
        var frames = 0;
        var terminal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.FrameProduced += (_, _) => Interlocked.Increment(ref frames);
        service.StatusChanged += (_, status) =>
        {
            if (status.State == AudioCaptureState.Unavailable)
                terminal.TrySetResult(null);
        };
        await service.StartAsync(null);
        runtime.EmitData(AudioTestData.ConstantPcm16Frame());
        await dispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.EmitStopped(AudioRuntimeStopReason.Unavailable, "device lost");
        releaseDispatch.TrySetResult(null);
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, frames);
    }

    [Fact]
    public async Task StartAfterStopUsesCleanDispatcherGeneration()
    {
        var firstRuntime = new FakeAudioCaptureRuntime();
        var secondRuntime = new FakeAudioCaptureRuntime();
        var endpoints = new FakeAudioEndpointProvider();
        var factory = new FakeAudioCaptureRuntimeFactory();
        factory.Enqueue(firstRuntime);
        factory.Enqueue(secondRuntime);
        await using var service = new AudioCaptureService(endpoints, factory);
        var frames = new List<NormalizedAudioFrame>();
        var frameReceived = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.FrameProduced += (_, frame) =>
        {
            frames.Add(frame);
            frameReceived.TrySetResult(null);
        };

        var firstStart = await service.StartAsync(null);
        firstRuntime.EmitData(AudioTestData.ConstantPcm16Frame());
        await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync();
        frameReceived = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStart = await service.StartAsync(null);
        firstRuntime.EmitData(AudioTestData.ConstantPcm16Frame());
        secondRuntime.EmitData(AudioTestData.ConstantPcm16Frame());
        await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, frames.Count);
        Assert.Equal(firstStart.SessionId, frames[0].SessionId);
        Assert.Equal(secondStart.SessionId, frames[1].SessionId);
        Assert.Equal(1, frames[1].Sequence);
    }

    [Fact]
    public async Task DispatcherFaultBecomesObservedTypedServiceFailure()
    {
        var runtime = new FakeAudioCaptureRuntime();
        await using var service = CreateService(
            runtime,
            beforeDispatch: () => throw new InvalidOperationException("dispatcher marker"));
        var terminal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) =>
        {
            if (status.State == AudioCaptureState.Faulted)
                terminal.TrySetResult(null);
        };
        await service.StartAsync(null);
        runtime.EmitData(AudioTestData.ConstantPcm16Frame());
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AudioCaptureState.Faulted, service.State);
        Assert.Contains("dispatcher marker", service.FailureReason);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.True(service.PublicationDispatchersCompleted);
    }

    private static AudioCaptureService CreateService(
        FakeAudioCaptureRuntime runtime,
        Action? beforeDispatch = null)
    {
        var endpoints = new FakeAudioEndpointProvider();
        var factory = new FakeAudioCaptureRuntimeFactory();
        factory.Enqueue(runtime);
        return new AudioCaptureService(endpoints, factory, null, beforeDispatch, null);
    }

    private static AudioCaptureService CreateService(DedicatedThreadAudioCaptureRuntime runtime)
    {
        var endpoints = new FakeAudioEndpointProvider();
        var factory = new DedicatedRuntimeFactory(runtime);
        return new AudioCaptureService(endpoints, factory);
    }

    private sealed class DedicatedRuntimeFactory(DedicatedThreadAudioCaptureRuntime runtime)
        : IAudioCaptureRuntimeFactory
    {
        public IAudioCaptureRuntime Create() => runtime;
    }
}
