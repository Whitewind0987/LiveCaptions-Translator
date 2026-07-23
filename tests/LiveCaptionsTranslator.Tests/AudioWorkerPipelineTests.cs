using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.ipc;
using LiveCaptionsTranslator.worker;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class AudioWorkerPipelineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CompleteFakePipelineStartsStreamsDrainsAndShutsDown()
    {
        var captureFactory = new FakeAudioCaptureRuntimeFactory();
        var capture = new AudioCaptureService(new FakeAudioEndpointProvider(), captureFactory);
        var worker = new PipelineWorkerFixture();
        await using var pipeline = new AudioWorkerPipeline(capture, worker.Supervisor);

        await pipeline.StartAsync(null, Token);
        captureFactory.Created[0].EmitData(AudioTestData.ConstantPcm16Frame());
        await WaitAsync(() => worker.Transports[0].Frames.Count == 1);
        await pipeline.StopAsync(Token);

        Assert.Equal(AudioWorkerPipelineState.Stopped, pipeline.State);
        Assert.Equal(1, pipeline.Diagnostics.Pump!.FramesSent);
        Assert.Equal(AudioFramePumpPhase.Completed, pipeline.Diagnostics.Pump.Phase);
        Assert.True(pipeline.Diagnostics.Pump.SourceCompletionObserved);
        Assert.False(pipeline.Diagnostics.Pump.OwnedCancellationUsed);
        Assert.True(pipeline.Diagnostics.PumpJoined);
        Assert.Equal(1, pipeline.Diagnostics.WorkerSummary!.FramesReceived);
        Assert.Equal(new[] { "connect", "start", "frame", "end", "stop", "shutdown" }, worker.Transports[0].Events);
    }

    [Fact]
    public async Task RepeatedPipelineStartStopCreatesFreshWorkerAndCaptureSessions()
    {
        var captureFactory = new FakeAudioCaptureRuntimeFactory();
        var capture = new AudioCaptureService(new FakeAudioEndpointProvider(), captureFactory);
        var worker = new PipelineWorkerFixture();
        await using var pipeline = new AudioWorkerPipeline(capture, worker.Supervisor);
        var sessions = new List<Guid>();

        for (var index = 0; index < 2; index++)
        {
            await pipeline.StartAsync(null, Token); sessions.Add(worker.Supervisor.SessionId!.Value);
            captureFactory.Created[index].EmitData(AudioTestData.ConstantPcm16Frame());
            await WaitAsync(() => worker.Transports[index].Frames.Count == 1);
            await pipeline.StopAsync(Token);
        }

        Assert.NotEqual(sessions[0], sessions[1]);
        Assert.Equal(2, worker.Processes.Count);
        Assert.All(worker.Processes, process => Assert.True(process.HasExited));
    }

    [Fact]
    public async Task PumpFailureImmediatelyFaultsPipelineAndStopsCaptureAndWorker()
    {
        var captureFactory = new FakeAudioCaptureRuntimeFactory();
        var capture = new AudioCaptureService(new FakeAudioEndpointProvider(), captureFactory);
        var worker = new PipelineWorkerFixture(failSend: true);
        await using var pipeline = new AudioWorkerPipeline(capture, worker.Supervisor);
        await pipeline.StartAsync(null, Token);
        captureFactory.Created[0].EmitData(AudioTestData.ConstantPcm16Frame());
        await WaitAsync(() => pipeline.State == AudioWorkerPipelineState.Faulted && worker.Processes[0].HasExited);
        Assert.Equal(AsrWorkerFailureKind.AudioPipeClosed, pipeline.Diagnostics.FailureKind);
        Assert.NotEqual(AudioCaptureState.Running, capture.State);
    }

    [Fact]
    public async Task MismatchedFinalSummaryMakesCleanStopFail()
    {
        var captureFactory = new FakeAudioCaptureRuntimeFactory();
        var capture = new AudioCaptureService(new FakeAudioEndpointProvider(), captureFactory);
        var worker = new PipelineWorkerFixture(badSummary: true);
        await using var pipeline = new AudioWorkerPipeline(capture, worker.Supervisor);
        await pipeline.StartAsync(null, Token);
        captureFactory.Created[0].EmitData(AudioTestData.ConstantPcm16Frame());
        await WaitAsync(() => worker.Transports[0].Frames.Count == 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.StopAsync(Token));
        Assert.Equal(AudioWorkerPipelineState.Faulted, pipeline.State);
        Assert.Equal(AsrWorkerFailureKind.ProtocolViolation, pipeline.Diagnostics.FailureKind);
    }

    [Fact]
    public async Task SourceCompletionRegressionIsTypedJoinedAndSkipsStreamCompletion()
    {
        var captureFactory = new FakeAudioCaptureRuntimeFactory();
        var capture = new AudioCaptureService(new FakeAudioEndpointProvider(), captureFactory);
        var worker = new PipelineWorkerFixture();
        var options = new AudioPumpDrainOptions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(5));
        await using var pipeline = new AudioWorkerPipeline(capture, worker.Supervisor, options, () => Task.CompletedTask);

        await pipeline.StartAsync(null, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.StopAsync(Token));

        var diagnostics = pipeline.Diagnostics;
        Assert.Equal(AudioWorkerPipelineState.Faulted, diagnostics.State);
        Assert.Equal(AsrWorkerFailureKind.AudioPumpFailed, diagnostics.FailureKind);
        Assert.Contains("waiting for source completion", diagnostics.FailureReason);
        Assert.Equal(AudioFramePumpPhase.Canceled, diagnostics.Pump!.Phase);
        Assert.True(diagnostics.Pump.OwnedCancellationUsed);
        Assert.True(diagnostics.PumpJoined);
        Assert.Empty(diagnostics.CleanupFailures);
        Assert.DoesNotContain(diagnostics.CleanupFailures, value => value.Contains("cancellation/join failed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("end", worker.Transports[0].Events);
        Assert.DoesNotContain("stop", worker.Transports[0].Events);
        await pipeline.DisposeAsync();
        Assert.True(worker.Processes[0].HasExited);
    }

    [Fact]
    public async Task StalledTransportWriteIsCanceledJoinedAndSkipsStreamCompletion()
    {
        var captureFactory = new FakeAudioCaptureRuntimeFactory();
        var capture = new AudioCaptureService(new FakeAudioEndpointProvider(), captureFactory);
        var worker = new PipelineWorkerFixture(stallSend: true);
        var options = new AudioPumpDrainOptions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(5));
        await using var pipeline = new AudioWorkerPipeline(capture, worker.Supervisor, options, null);

        await pipeline.StartAsync(null, Token);
        captureFactory.Created[0].EmitData(AudioTestData.ConstantPcm16Frame());
        await WaitAsync(() => pipeline.Diagnostics.Pump?.Phase == AudioFramePumpPhase.WritingFrame);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.StopAsync(Token));

        var diagnostics = pipeline.Diagnostics;
        Assert.Equal(AsrWorkerFailureKind.AudioPumpFailed, diagnostics.FailureKind);
        Assert.Contains("writing frame", diagnostics.FailureReason);
        Assert.Equal(AudioFramePumpPhase.Canceled, diagnostics.Pump!.Phase);
        Assert.True(diagnostics.Pump.OwnedCancellationUsed);
        Assert.True(diagnostics.PumpJoined);
        Assert.Equal(0, diagnostics.Pump.FramesSent);
        Assert.Empty(diagnostics.CleanupFailures);
        Assert.DoesNotContain(diagnostics.CleanupFailures, value => value.Contains("cancellation/join failed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("end", worker.Transports[0].Events);
        Assert.DoesNotContain("stop", worker.Transports[0].Events);
        await pipeline.DisposeAsync();
        Assert.True(worker.Processes[0].HasExited);
    }

    [Fact]
    public async Task CanceledCallerTokenDoesNotSkipMandatoryNormalCleanup()
    {
        var captureFactory = new FakeAudioCaptureRuntimeFactory();
        var capture = new AudioCaptureService(new FakeAudioEndpointProvider(), captureFactory);
        var worker = new PipelineWorkerFixture();
        await using var pipeline = new AudioWorkerPipeline(capture, worker.Supervisor);
        await pipeline.StartAsync(null, Token);
        captureFactory.Created[0].EmitData(AudioTestData.ConstantPcm16Frame());
        await WaitAsync(() => worker.Transports[0].Frames.Count == 1);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await pipeline.StopAsync(canceled.Token);
        await pipeline.DisposeAsync();

        Assert.Equal(AudioWorkerPipelineState.Stopped, pipeline.State);
        Assert.Equal(AudioCaptureState.Stopped, capture.State);
        Assert.Equal(new[] { "connect", "start", "frame", "end", "stop", "shutdown" }, worker.Transports[0].Events);
        Assert.True(worker.Processes[0].HasExited);
    }

    [Fact]
    public async Task ForwardProgressExtendsDrainBeyondOneStallWindow()
    {
        var captureFactory = new FakeAudioCaptureRuntimeFactory();
        var capture = new AudioCaptureService(new FakeAudioEndpointProvider(), captureFactory);
        var worker = new PipelineWorkerFixture(sendDelay: TimeSpan.FromMilliseconds(20));
        var options = new AudioPumpDrainOptions(TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(5));
        await using var pipeline = new AudioWorkerPipeline(capture, worker.Supervisor, options, null);
        await pipeline.StartAsync(null, Token);
        for (var index = 0; index < 6; index++) captureFactory.Created[0].EmitData(AudioTestData.ConstantPcm16Frame());

        await pipeline.StopAsync(Token);

        Assert.Equal(AudioWorkerPipelineState.Stopped, pipeline.State);
        Assert.Equal(6, pipeline.Diagnostics.Pump!.FramesSent);
        Assert.True(pipeline.Diagnostics.PumpJoined);
        Assert.False(pipeline.Diagnostics.Pump.OwnedCancellationUsed);
    }

    [Fact]
    public async Task OwnedWorkerExitTreatsResultingAudioPipeClosureAsExpectedPumpCleanup()
    {
        var captureFactory = new FakeAudioCaptureRuntimeFactory();
        var capture = new AudioCaptureService(new FakeAudioEndpointProvider(), captureFactory);
        var worker = new PipelineWorkerFixture(failPendingSendOnDispose: true);
        await using var pipeline = new AudioWorkerPipeline(capture, worker.Supervisor);
        await pipeline.StartAsync(null, Token);
        captureFactory.Created[0].EmitData(AudioTestData.ConstantPcm16Frame());
        await WaitAsync(() => pipeline.Diagnostics.Pump?.Phase == AudioFramePumpPhase.WritingFrame);

        worker.Processes[0].Exit(-1);
        await WaitAsync(() => pipeline.State == AudioWorkerPipelineState.Faulted && pipeline.Diagnostics.PumpJoined);

        var diagnostics = pipeline.Diagnostics;
        Assert.Equal(AsrWorkerFailureKind.WorkerExited, diagnostics.FailureKind);
        Assert.Empty(diagnostics.CleanupFailures);
        Assert.True(worker.Processes[0].HasExited);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5, Token);
        Assert.True(condition());
    }

    private sealed class PipelineWorkerFixture
    {
        public List<PipelineProcess> Processes { get; } = [];
        public List<PipelineTransport> Transports { get; } = [];
        public AsrWorkerSupervisor Supervisor { get; }
        private readonly bool failSend;
        private readonly bool badSummary;
        private readonly bool stallSend;
        private readonly TimeSpan sendDelay;
        private readonly bool failPendingSendOnDispose;
        public PipelineWorkerFixture(bool failSend = false, bool badSummary = false, bool stallSend = false, TimeSpan sendDelay = default, bool failPendingSendOnDispose = false)
        {
            this.failSend = failSend;
            this.badSummary = badSummary;
            this.stallSend = stallSend;
            this.sendDelay = sendDelay;
            this.failPendingSendOnDispose = failPendingSendOnDispose;
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "AGENTS.md"));
            Supervisor = new AsrWorkerSupervisor(path, new Launcher(this), new JobFactory(), new TransportFactory(this), TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(100));
        }
        private sealed class Launcher(PipelineWorkerFixture owner) : IWorkerProcessLauncher
        {
            public Task<IWorkerProcess> LaunchAsync(WorkerLaunchRequest request, CancellationToken cancellationToken = default) { var value = new PipelineProcess(2000 + owner.Processes.Count); owner.Processes.Add(value); return Task.FromResult<IWorkerProcess>(value); }
        }
        private sealed class TransportFactory(PipelineWorkerFixture owner) : IAsrWorkerTransportFactory
        {
            public IAsrWorkerTransport Create(WorkerPipeIdentity identity) { var value = new PipelineTransport(owner, owner.failSend, owner.badSummary, owner.stallSend, owner.sendDelay, owner.failPendingSendOnDispose); owner.Transports.Add(value); return value; }
        }
    }

    private sealed class PipelineProcess(int id) : IWorkerProcess
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously); private int? code;
        public int Id => id; public nint NativeHandle => 1; public bool HasExited => completion.Task.IsCompleted; public int? ExitCode => code; public Task Completion => completion.Task; public IReadOnlyList<string> RecentStdout => []; public IReadOnlyList<string> RecentStderr => [];
        public void Exit(int value) { code = value; completion.TrySetResult(); }
        public Task TerminateTreeAsync(CancellationToken cancellationToken = default) { Exit(-1); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { if (!HasExited) Exit(0); return ValueTask.CompletedTask; }
    }
    private sealed class JobFactory : IWorkerJobFactory { public IWorkerJob Create() => new Job(); }
    private sealed class Job : IWorkerJob { public bool AssignmentSucceeded { get; private set; } public string? FailureReason => null; public Task AssignAsync(IWorkerProcess process, CancellationToken cancellationToken = default) { AssignmentSucceeded = true; return Task.CompletedTask; } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

    private sealed class PipelineTransport(PipelineWorkerFixture owner, bool failSend, bool badSummary, bool stallSend, TimeSpan sendDelay, bool failPendingSendOnDispose) : IAsrWorkerTransport
    {
        private readonly TaskCompletionSource pendingSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Guid captureSession; public List<string> Events { get; } = []; public List<NormalizedAudioFrame> Frames { get; } = [];
        public event EventHandler<AudioStreamSummaryPayload>? ProgressReceived { add { } remove { } } public event EventHandler<ErrorPayload>? ErrorReceived { add { } remove { } }
        public long ControlMessagesSent => 0; public long ControlMessagesReceived => 0; public long AudioFramesSent => Frames.Count; public long AudioBytesSent => Frames.Sum(frame => frame.Payload.Length); public DateTimeOffset? LatestPongAtUtc => DateTimeOffset.UtcNow;
        public Task<WorkerTransportFailure> TerminalFailure { get; } = new TaskCompletionSource<WorkerTransportFailure>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        public Task<WorkerTransportStartResult> ConnectAndHandshakeAsync(int expectedPid, CancellationToken cancellationToken = default) { Events.Add("connect"); return Task.FromResult(new WorkerTransportStartResult(0, WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink, TimeSpan.Zero)); }
        public Task StartAudioStreamAsync(StartAudioStreamPayload request, CancellationToken cancellationToken = default) { captureSession = request.CaptureSessionId; Events.Add("start"); return Task.CompletedTask; }
        public async Task SendAudioFrameAsync(Guid workerSessionId, NormalizedAudioFrame frame, CancellationToken cancellationToken = default) { if (failSend) throw new WorkerTransportException(AsrWorkerFailureKind.AudioPipeClosed, "audio closed"); if (failPendingSendOnDispose) await pendingSend.Task; if (stallSend) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); if (sendDelay > TimeSpan.Zero) await Task.Delay(sendDelay, cancellationToken); Frames.Add(frame); Events.Add("frame"); }
        public Task EndAudioStreamAsync(AudioStreamEndPayload end, CancellationToken cancellationToken = default) { Events.Add("end"); return Task.CompletedTask; }
        public Task<AudioStreamSummaryPayload> StopAudioStreamAsync(Guid workerSessionId, Guid captureSessionId, CancellationToken cancellationToken = default) { Events.Add("stop"); var count = badSummary ? Frames.Count + 1 : Frames.Count; return Task.FromResult(new AudioStreamSummaryPayload(captureSession, count, count * 640L, Frames.FirstOrDefault()?.Sequence ?? 0, Frames.LastOrDefault()?.Sequence ?? 0, 0, 0, Frames.FirstOrDefault()?.CapturedAtUtc.ToUnixTimeMilliseconds() ?? 0, Frames.LastOrDefault()?.CapturedAtUtc.ToUnixTimeMilliseconds() ?? 0)); }
        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ShutdownAsync(Guid workerSessionId, CancellationToken cancellationToken = default) { Events.Add("shutdown"); owner.Processes[^1].Exit(0); return Task.FromResult(true); }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { if (failPendingSendOnDispose) pendingSend.TrySetException(new WorkerTransportException(AsrWorkerFailureKind.AudioPipeClosed, "audio closed during worker cleanup")); return ValueTask.CompletedTask; }
    }
}
