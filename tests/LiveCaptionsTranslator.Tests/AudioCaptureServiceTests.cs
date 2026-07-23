using System.Threading.Channels;

using LiveCaptionsTranslator.audio;
using Xunit;

#pragma warning disable xUnit1051

namespace LiveCaptionsTranslator.Tests;

public sealed class AudioCaptureServiceTests
{
    [Fact]
    public async Task InitialStateIsStoppedWithEmptyDiagnostics()
    {
        await using var service = CreateService(out _, out _);
        Assert.Equal(AudioCaptureState.Stopped, service.State);
        Assert.Null(service.Diagnostics.SessionId);
        Assert.Equal(0, service.Diagnostics.FramesProduced);
        Assert.Equal(0, service.FrameBuffer.Count);
    }

    [Fact]
    public async Task SuccessfulStartTransitionsAndRetainsResolution()
    {
        await using var service = CreateService(out var endpoints, out var factory);
        var statuses = new List<AudioCaptureState>();
        service.StatusChanged += (_, status) => statuses.Add(status.State);
        var result = await service.StartAsync("saved-id");
        Assert.True(result.Success);
        Assert.Equal(AudioCaptureState.Running, service.State);
        Assert.Equal(FakeAudioEndpointProvider.SavedEndpoint, result.Endpoint);
        Assert.Equal(new[] { AudioCaptureState.Starting, AudioCaptureState.Running }, statuses);
        Assert.Equal(1, endpoints.ResolveCount);
        Assert.Equal(1, factory.Created.Single().StartCount);
    }

    [Fact]
    public async Task DuplicateStartUsesSameSessionAndRuntime()
    {
        await using var service = CreateService(out _, out var factory);
        var first = await service.StartAsync(null);
        var second = await service.StartAsync(null);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Single(factory.Created);
        Assert.Equal(1, factory.Created.Single().StartCount);
    }

    [Fact]
    public async Task FallbackDiagnosticIsRetained()
    {
        await using var service = CreateService(out _, out _);
        var result = await service.StartAsync("missing-id");
        Assert.True(result.UsedEndpointFallback);
        Assert.Contains("missing-id", result.Diagnostic);
        Assert.True(service.Diagnostics.UsedEndpointFallback);
    }

    [Fact]
    public async Task RawCallbackProducesNormalizedFixedFrameAndDiagnostics()
    {
        await using var service = CreateService(out _, out var factory);
        await service.StartAsync(null);
        factory.Created.Single().EmitData(AudioTestData.ConstantPcm16Frame());
        var frame = await service.FrameBuffer.ReadAsync();
        Assert.Equal(640, frame.Payload.Length);
        Assert.Equal(1, frame.Sequence);
        Assert.Equal(0, frame.StartSampleIndex);
        var diagnostics = service.Diagnostics;
        Assert.Equal(1, diagnostics.FramesProduced);
        Assert.Equal(1, diagnostics.FramesConsumed);
        Assert.Equal(640, diagnostics.InputBytesReceived);
        Assert.Equal(640, diagnostics.NormalizedBytesProduced);
        Assert.Equal(1, diagnostics.LastFrameSequence);
    }

    [Fact]
    public async Task CallbackAfterStopIsIgnoredAndReadCompletes()
    {
        await using var service = CreateService(out _, out var factory);
        await service.StartAsync(null);
        var buffer = service.FrameBuffer;
        await service.StopAsync();
        factory.Created.Single().EmitData(AudioTestData.ConstantPcm16Frame());
        Assert.Equal(0, service.Diagnostics.FramesProduced);
        await Assert.ThrowsAsync<ChannelClosedException>(() => buffer.ReadAsync().AsTask());
    }

    [Theory]
    [InlineData(AudioRuntimeStopReason.Unavailable, AudioCaptureState.Unavailable)]
    [InlineData(AudioRuntimeStopReason.Faulted, AudioCaptureState.Faulted)]
    public async Task UnexpectedRuntimeStopMapsTypedState(
        AudioRuntimeStopReason reason,
        AudioCaptureState expected)
    {
        await using var service = CreateService(out _, out var factory);
        await service.StartAsync(null);
        factory.Created.Single().EmitStopped(reason, "device stopped");
        Assert.Equal(expected, service.State);
        Assert.Contains("device stopped", service.FailureReason);
        Assert.True(service.FrameBuffer.IsCompleted);
    }

    [Fact]
    public async Task ExpectedRuntimeStopDoesNotFaultService()
    {
        await using var service = CreateService(out _, out var factory);
        await service.StartAsync(null);
        factory.Created.Single().EmitStopped(AudioRuntimeStopReason.Expected);
        Assert.Equal(AudioCaptureState.Running, service.State);
    }

    [Theory]
    [InlineData(AudioCaptureState.Unavailable)]
    [InlineData(AudioCaptureState.Faulted)]
    public async Task FailedOpenCleansRuntimeAndPublishesNoFrames(AudioCaptureState failureState)
    {
        var runtime = new FakeAudioCaptureRuntime
        {
            OpenResult = AudioRuntimeOpenResult.Failed(failureState, "open failed")
        };
        await using var service = CreateService(out _, out var factory, runtime);
        var result = await service.StartAsync(null);
        Assert.False(result.Success);
        Assert.Equal(failureState, service.State);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(0, service.Diagnostics.FramesProduced);
        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task UnsupportedFormatFailsAndCleansRuntime()
    {
        var runtime = new FakeAudioCaptureRuntime
        {
            OpenResult = AudioRuntimeOpenResult.Opened(
                new AudioInputFormat(16000, 1, 8, 1, AudioSampleEncoding.PcmSignedInteger))
        };
        await using var service = CreateService(out _, out _, runtime);
        var result = await service.StartAsync(null);
        Assert.False(result.Success);
        Assert.Equal(AudioCaptureState.Unavailable, result.State);
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task StopIsIdempotentAndConcurrentStopPerformsOneCleanup()
    {
        await using var service = CreateService(out _, out var factory);
        await service.StartAsync(null);
        await Task.WhenAll(service.StopAsync(), service.StopAsync());
        var runtime = factory.Created.Single();
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(AudioCaptureState.Stopped, service.State);
    }

    [Fact]
    public async Task StartAfterStopCreatesNewSessionAndEmptyBuffer()
    {
        await using var service = CreateService(out _, out var factory);
        var first = await service.StartAsync(null);
        factory.Created[0].EmitData(AudioTestData.ConstantPcm16Frame());
        await service.StopAsync();
        var second = await service.StartAsync(null);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(0, service.FrameBuffer.Count);
        Assert.Equal(2, factory.Created.Count);
    }

    [Fact]
    public async Task SubscriberExceptionDoesNotCorruptCapture()
    {
        await using var service = CreateService(out _, out var factory);
        service.StatusChanged += (_, _) => throw new InvalidOperationException("status subscriber");
        service.FrameProduced += (_, _) => throw new InvalidOperationException("frame subscriber");
        var result = await service.StartAsync(null);
        factory.Created.Single().EmitData(AudioTestData.ConstantPcm16Frame());
        Assert.True(result.Success);
        Assert.Equal(1, service.Diagnostics.FramesProduced);
    }

    [Fact]
    public async Task StaleCallbackFromPreviousRuntimeIsRejected()
    {
        await using var service = CreateService(out _, out var factory);
        await service.StartAsync(null);
        var oldRuntime = factory.Created[0];
        await service.StopAsync();
        await service.StartAsync(null);
        oldRuntime.EmitData(AudioTestData.ConstantPcm16Frame());
        Assert.Equal(0, service.Diagnostics.FramesProduced);
    }

    [Fact]
    public async Task RepeatedDisposalIsSafeAndDisposesProvider()
    {
        var endpoints = new FakeAudioEndpointProvider();
        var service = new AudioCaptureService(endpoints, new FakeAudioCaptureRuntimeFactory());
        await service.StartAsync(null);
        await service.DisposeAsync();
        await service.DisposeAsync();
        Assert.Equal(1, endpoints.DisposeCount);
    }

    private static AudioCaptureService CreateService(
        out FakeAudioEndpointProvider endpoints,
        out FakeAudioCaptureRuntimeFactory factory,
        FakeAudioCaptureRuntime? runtime = null)
    {
        endpoints = new FakeAudioEndpointProvider();
        factory = new FakeAudioCaptureRuntimeFactory();
        if (runtime != null)
            factory.Enqueue(runtime);
        return new AudioCaptureService(endpoints, factory);
    }
}
