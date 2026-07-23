using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.buffering;
using LiveCaptionsTranslator.ipc;
using LiveCaptionsTranslator.worker;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class AudioFramePumpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task PumpPreservesOrderBytesAndIdentityAndDrainsCompletion()
    {
        var capture = Guid.NewGuid(); var worker = Guid.NewGuid(); var buffer = new BoundedAudioFrameBuffer(4); var transport = new RecordingTransport();
        buffer.TryWrite(Frame(capture, 1)); buffer.TryWrite(Frame(capture, 2)); buffer.Complete();
        var pump = new AudioFramePump(buffer, transport, worker, capture, 1);
        await pump.RunAsync(Token);
        Assert.Equal(new long[] { 1, 2 }, transport.Frames.Select(frame => frame.Sequence));
        Assert.All(transport.WorkerSessions, value => Assert.Equal(worker, value));
        Assert.Equal(2, pump.Diagnostics.FramesSent);
        Assert.Equal(1280, pump.Diagnostics.BytesSent);
        Assert.Equal(0, pump.Diagnostics.SourceSequenceGaps);
        Assert.Equal(AudioFramePumpPhase.Completed, pump.Diagnostics.Phase);
        Assert.True(pump.Diagnostics.SourceCompletionObserved);
        Assert.False(pump.Diagnostics.OwnedCancellationUsed);
    }

    [Fact]
    public async Task PumpRetainsSourceSequenceGaps()
    {
        var capture = Guid.NewGuid(); var buffer = new BoundedAudioFrameBuffer(4); var transport = new RecordingTransport();
        buffer.TryWrite(Frame(capture, 2)); buffer.TryWrite(Frame(capture, 5)); buffer.Complete();
        var pump = new AudioFramePump(buffer, transport, Guid.NewGuid(), capture, 2);
        await pump.RunAsync(Token);
        Assert.Equal(2, pump.Diagnostics.SourceSequenceGaps);
    }

    [Fact]
    public async Task PumpCountsGapBeforeFirstSentFrameFromInitialSequence()
    {
        var capture = Guid.NewGuid(); var buffer = new BoundedAudioFrameBuffer(2); var transport = new RecordingTransport();
        buffer.TryWrite(Frame(capture, 3)); buffer.Complete();
        var pump = new AudioFramePump(buffer, transport, Guid.NewGuid(), capture, 1);
        await pump.RunAsync(Token);
        Assert.Equal(2, pump.Diagnostics.SourceSequenceGaps);
        Assert.Equal(3, pump.Diagnostics.FirstSequence);
    }

    [Fact]
    public async Task StaleCaptureSessionIsRejected()
    {
        var buffer = new BoundedAudioFrameBuffer(2); buffer.TryWrite(Frame(Guid.NewGuid(), 1)); buffer.Complete();
        var pump = new AudioFramePump(buffer, new RecordingTransport(), Guid.NewGuid(), Guid.NewGuid(), 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pump.RunAsync(Token));
    }

    [Fact]
    public async Task CancellationWakesWaitingPump()
    {
        using var cancellation = new CancellationTokenSource(); var pump = new AudioFramePump(new BoundedAudioFrameBuffer(), new RecordingTransport(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var run = pump.RunAsync(cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task OwnedCancellationIsRetainedAsJoinedPumpDiagnostics()
    {
        using var cancellation = new CancellationTokenSource();
        var pump = new AudioFramePump(new BoundedAudioFrameBuffer(), new RecordingTransport(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var run = pump.RunAsync(cancellation.Token);
        pump.MarkOwnedCancellationRequested("Test-owned cancellation.");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(AudioFramePumpPhase.Canceled, pump.Diagnostics.Phase);
        Assert.True(pump.Diagnostics.OwnedCancellationUsed);
        Assert.False(pump.Diagnostics.SourceCompletionObserved);
        Assert.Equal("Test-owned cancellation.", pump.Diagnostics.FailureReason);
    }

    [Fact]
    public async Task PipeFailureStopsPumpWithoutBacklog()
    {
        var capture = Guid.NewGuid(); var buffer = new BoundedAudioFrameBuffer(2); var transport = new RecordingTransport { FailAfter = 1 };
        buffer.TryWrite(Frame(capture, 1)); buffer.TryWrite(Frame(capture, 2)); buffer.Complete();
        var pump = new AudioFramePump(buffer, transport, Guid.NewGuid(), capture, 1);
        await Assert.ThrowsAsync<IOException>(() => pump.RunAsync(Token));
        Assert.Single(transport.Frames);
        Assert.NotNull(pump.Diagnostics.FailureReason);
    }

    private static NormalizedAudioFrame Frame(Guid session, long sequence) => new(session, sequence, (sequence - 1) * 320, DateTimeOffset.UtcNow.AddMilliseconds(sequence), new byte[640]);

    private sealed class RecordingTransport : IAsrWorkerTransport
    {
        public List<Guid> WorkerSessions { get; } = [];
        public List<NormalizedAudioFrame> Frames { get; } = [];
        public int FailAfter { get; init; } = int.MaxValue;
        public event EventHandler<AudioStreamSummaryPayload>? ProgressReceived { add { } remove { } }
        public event EventHandler<ErrorPayload>? ErrorReceived { add { } remove { } }
        public long ControlMessagesSent => 0; public long ControlMessagesReceived => 0; public long AudioFramesSent => Frames.Count; public long AudioBytesSent => Frames.Sum(frame => frame.Payload.Length); public DateTimeOffset? LatestPongAtUtc => null;
        public Task<WorkerTransportFailure> TerminalFailure { get; } = new TaskCompletionSource<WorkerTransportFailure>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        public Task<WorkerTransportStartResult> ConnectAndHandshakeAsync(int expectedPid, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task StartAudioStreamAsync(StartAudioStreamPayload request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAudioFrameAsync(Guid workerSessionId, NormalizedAudioFrame frame, CancellationToken cancellationToken = default)
        {
            if (Frames.Count >= FailAfter) throw new IOException("pipe closed"); WorkerSessions.Add(workerSessionId); Frames.Add(frame); return Task.CompletedTask;
        }
        public Task EndAudioStreamAsync(AudioStreamEndPayload end, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AudioStreamSummaryPayload> StopAudioStreamAsync(Guid workerSessionId, Guid captureSessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ShutdownAsync(Guid workerSessionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
