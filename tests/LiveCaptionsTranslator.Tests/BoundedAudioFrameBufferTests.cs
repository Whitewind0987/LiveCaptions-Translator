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
    public async Task OneBufferedFrameDrainsAndThenCloses()
    {
        using var buffer = new BoundedAudioFrameBuffer();
        buffer.TryWrite(Frame(1));
        buffer.Complete();
        Assert.Equal(new long[] { 1 }, await DrainAsync(buffer));
    }

    [Fact]
    public async Task ManyBufferedFramesDrainAndThenClose()
    {
        using var buffer = new BoundedAudioFrameBuffer();
        for (var sequence = 1; sequence <= 20; sequence++) buffer.TryWrite(Frame(sequence));
        buffer.Complete();
        Assert.Equal(Enumerable.Range(1, 20).Select(value => (long)value), await DrainAsync(buffer));
    }

    [Fact]
    public async Task FullDefaultBufferDrainsAndThenCloses()
    {
        using var buffer = new BoundedAudioFrameBuffer();
        for (var sequence = 1; sequence <= BoundedAudioFrameBuffer.DefaultCapacity; sequence++) buffer.TryWrite(Frame(sequence));
        buffer.Complete();
        Assert.Equal(BoundedAudioFrameBuffer.DefaultCapacity, (await DrainAsync(buffer)).Count);
    }

    [Fact]
    public async Task DroppedFramesDoNotPreventRemainingFramesFromDraining()
    {
        using var buffer = new BoundedAudioFrameBuffer(3);
        for (var sequence = 1; sequence <= 8; sequence++) buffer.TryWrite(Frame(sequence));
        buffer.Complete();
        Assert.Equal(new long[] { 6, 7, 8 }, await DrainAsync(buffer));
        Assert.Equal(5, buffer.DroppedCount);
    }

    [Fact]
    public async Task CompleteConcurrentWithBlockedReadWakesReader()
    {
        using var buffer = new BoundedAudioFrameBuffer();
        var read = Task.Run(async () => await Assert.ThrowsAsync<ChannelClosedException>(() => buffer.ReadAsync().AsTask()));
        await Task.Yield();
        buffer.Complete();
        await read.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CompleteConcurrentWithFinalReadLeavesNoBlockedConsumer()
    {
        using var buffer = new BoundedAudioFrameBuffer();
        buffer.TryWrite(Frame(1));
        var finalRead = buffer.ReadAsync().AsTask();
        buffer.Complete();
        Assert.Equal(1, (await finalRead).Sequence);
        await Assert.ThrowsAsync<ChannelClosedException>(() => buffer.ReadAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RepeatedCompleteIsIdempotentAndWakesReader()
    {
        using var buffer = new BoundedAudioFrameBuffer();
        var read = buffer.ReadAsync().AsTask();
        buffer.Complete();
        buffer.Complete();
        buffer.Complete();
        await Assert.ThrowsAsync<ChannelClosedException>(() => read).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ConsumerDoesNotRemainBlockedAfterAllQueuedFramesDrain()
    {
        using var buffer = new BoundedAudioFrameBuffer(5);
        for (var sequence = 1; sequence <= 5; sequence++) buffer.TryWrite(Frame(sequence));
        buffer.Complete();
        var drained = await DrainAsync(buffer).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(5, drained.Count);
        Assert.True(buffer.IsCompleted);
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

    private static async Task<IReadOnlyList<long>> DrainAsync(BoundedAudioFrameBuffer buffer)
    {
        var sequences = new List<long>();
        while (true)
        {
            try { sequences.Add((await buffer.ReadAsync()).Sequence); }
            catch (ChannelClosedException) { return sequences; }
        }
    }
}
