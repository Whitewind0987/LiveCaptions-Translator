using System.Threading.Channels;

using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.buffering;
using Xunit;

#pragma warning disable xUnit1051 // Tests supply explicit cancellation or complete immediately.

namespace LiveCaptionsTranslator.Tests;

public sealed class BoundedAudioFrameBufferTests
{
    [Fact]
    public void CapacityDropsOldestAcceptsNewestAndCountsExactly()
    {
        using var buffer = new BoundedAudioFrameBuffer(2);
        Assert.True(buffer.TryWrite(Frame(1)));
        Assert.True(buffer.TryWrite(Frame(2)));
        Assert.True(buffer.TryWrite(Frame(3)));
        Assert.Equal(2, buffer.Count);
        Assert.Equal(1, buffer.DroppedCount);
        Assert.True(buffer.TryRead(out var first));
        Assert.True(buffer.TryRead(out var second));
        Assert.Equal(2, first!.Sequence);
        Assert.Equal(3, second!.Sequence);
    }

    [Fact]
    public async Task ReadAsyncIsCancellationAware()
    {
        using var buffer = new BoundedAudioFrameBuffer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await buffer.ReadAsync(cancellation.Token));
    }

    [Fact]
    public async Task CompletionWakesWaitingConsumer()
    {
        using var buffer = new BoundedAudioFrameBuffer();
        var waiting = buffer.ReadAsync().AsTask();
        buffer.Complete();
        await Assert.ThrowsAsync<ChannelClosedException>(() => waiting);
    }

    [Fact]
    public async Task BufferedFramesDrainBeforeCompletion()
    {
        using var buffer = new BoundedAudioFrameBuffer();
        buffer.TryWrite(Frame(1));
        buffer.Complete();
        Assert.Equal(1, (await buffer.ReadAsync()).Sequence);
        await Assert.ThrowsAsync<ChannelClosedException>(() => buffer.ReadAsync().AsTask());
    }

    [Fact]
    public void CompletedBufferRejectsNewFramesAndNeverGrows()
    {
        using var buffer = new BoundedAudioFrameBuffer(3);
        for (var sequence = 1; sequence <= 100; sequence++)
            buffer.TryWrite(Frame(sequence));
        Assert.Equal(3, buffer.Count);
        Assert.Equal(97, buffer.DroppedCount);
        buffer.Complete();
        Assert.False(buffer.TryWrite(Frame(101)));
    }

    private static NormalizedAudioFrame Frame(long sequence) => new(
        new Guid("33333333-3333-3333-3333-333333333333"),
        sequence,
        (sequence - 1) * 320,
        new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero),
        new byte[640]);
}
