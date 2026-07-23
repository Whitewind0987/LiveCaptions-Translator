using System.Threading.Channels;

using LiveCaptionsTranslator.audio;
using Xunit;

#pragma warning disable xUnit1051

namespace LiveCaptionsTranslator.Tests;

public sealed class AudioCaptureDisposalTests
{
    [Fact]
    public async Task EndpointDisposalFailureDoesNotSkipBufferOrDispatcherCleanup()
    {
        var endpoints = new FakeAudioEndpointProvider
        {
            DisposeException = new InvalidOperationException("endpoint marker")
        };
        var service = CreateService(endpoints, new FakeAudioCaptureRuntime());
        await service.StartAsync(null);
        var waitingRead = service.FrameBuffer.ReadAsync().AsTask();

        var failure = await Assert.ThrowsAsync<AggregateException>(() => service.DisposeAsync().AsTask());

        Assert.Contains("endpoint marker", failure.ToString());
        Assert.Equal(1, endpoints.DisposeCount);
        Assert.True(service.PublicationDispatchersCompleted);
        await Assert.ThrowsAsync<ChannelClosedException>(() => waitingRead);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task RuntimeStopFailureDoesNotSkipEndpointCleanup()
    {
        var endpoints = new FakeAudioEndpointProvider();
        var runtime = new FakeAudioCaptureRuntime
        {
            StopException = new InvalidOperationException("stop marker")
        };
        var service = CreateService(endpoints, runtime);
        await service.StartAsync(null);

        await service.DisposeAsync();

        Assert.Equal(1, endpoints.DisposeCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Contains("stop marker", service.FailureReason);
    }

    [Fact]
    public async Task MultipleDisposalFailuresAreRetainedAndWaitingReaderIsReleased()
    {
        var endpoints = new FakeAudioEndpointProvider
        {
            DisposeException = new InvalidOperationException("endpoint marker")
        };
        var factory = new FakeAudioCaptureRuntimeFactory();
        factory.Enqueue(new FakeAudioCaptureRuntime());
        var service = new AudioCaptureService(
            endpoints,
            factory,
            null,
            null,
            () => throw new InvalidOperationException("buffer marker"));
        await service.StartAsync(null);
        var waitingRead = service.FrameBuffer.ReadAsync().AsTask();

        var failure = await Assert.ThrowsAsync<AggregateException>(() => service.DisposeAsync().AsTask());

        Assert.Contains("endpoint marker", failure.ToString());
        Assert.Contains("buffer marker", failure.ToString());
        Assert.Contains("Endpoint provider disposal failed", service.FailureReason);
        Assert.Contains("Frame buffer disposal preparation failed", service.FailureReason);
        await Assert.ThrowsAsync<ChannelClosedException>(() => waitingRead);
    }

    private static AudioCaptureService CreateService(
        FakeAudioEndpointProvider endpoints,
        FakeAudioCaptureRuntime runtime)
    {
        var factory = new FakeAudioCaptureRuntimeFactory();
        factory.Enqueue(runtime);
        return new AudioCaptureService(endpoints, factory);
    }
}
