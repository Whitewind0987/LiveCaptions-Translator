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
        Assert.Equal(1, pipeline.Diagnostics.WorkerSummary!.FramesReceived);
        Assert.Equal(new[] { "connect", "start", "frame", "stop", "shutdown" }, worker.Transports[0].Events);
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
        Assert.Equal(AsrWorkerFailureKind.CleanupFailed, pipeline.Diagnostics.FailureKind);
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
        public PipelineWorkerFixture(bool failSend = false, bool badSummary = false)
        {
            this.failSend = failSend;
            this.badSummary = badSummary;
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "AGENTS.md"));
            Supervisor = new AsrWorkerSupervisor(path, new Launcher(this), new JobFactory(), new TransportFactory(this), TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(100));
        }
        private sealed class Launcher(PipelineWorkerFixture owner) : IWorkerProcessLauncher
        {
            public Task<IWorkerProcess> LaunchAsync(WorkerLaunchRequest request, CancellationToken cancellationToken = default) { var value = new PipelineProcess(2000 + owner.Processes.Count); owner.Processes.Add(value); return Task.FromResult<IWorkerProcess>(value); }
        }
        private sealed class TransportFactory(PipelineWorkerFixture owner) : IAsrWorkerTransportFactory
        {
            public IAsrWorkerTransport Create(WorkerPipeIdentity identity) { var value = new PipelineTransport(owner, owner.failSend, owner.badSummary); owner.Transports.Add(value); return value; }
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

    private sealed class PipelineTransport(PipelineWorkerFixture owner, bool failSend, bool badSummary) : IAsrWorkerTransport
    {
        private Guid captureSession; public List<string> Events { get; } = []; public List<NormalizedAudioFrame> Frames { get; } = [];
        public event EventHandler<AudioStreamSummaryPayload>? ProgressReceived { add { } remove { } } public event EventHandler<ErrorPayload>? ErrorReceived { add { } remove { } }
        public long ControlMessagesSent => 0; public long ControlMessagesReceived => 0; public long AudioFramesSent => Frames.Count; public long AudioBytesSent => Frames.Sum(frame => frame.Payload.Length); public DateTimeOffset? LatestPongAtUtc => DateTimeOffset.UtcNow;
        public Task<WorkerTransportFailure> TerminalFailure { get; } = new TaskCompletionSource<WorkerTransportFailure>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        public Task<WorkerTransportStartResult> ConnectAndHandshakeAsync(int expectedPid, CancellationToken cancellationToken = default) { Events.Add("connect"); return Task.FromResult(new WorkerTransportStartResult(0, WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink, TimeSpan.Zero)); }
        public Task StartAudioStreamAsync(StartAudioStreamPayload request, CancellationToken cancellationToken = default) { captureSession = request.CaptureSessionId; Events.Add("start"); return Task.CompletedTask; }
        public Task SendAudioFrameAsync(Guid workerSessionId, NormalizedAudioFrame frame, CancellationToken cancellationToken = default) { if (failSend) throw new WorkerTransportException(AsrWorkerFailureKind.AudioPipeClosed, "audio closed"); Frames.Add(frame); Events.Add("frame"); return Task.CompletedTask; }
        public Task<AudioStreamSummaryPayload> StopAudioStreamAsync(Guid workerSessionId, Guid captureSessionId, CancellationToken cancellationToken = default) { Events.Add("stop"); var count = badSummary ? Frames.Count + 1 : Frames.Count; return Task.FromResult(new AudioStreamSummaryPayload(captureSession, count, count * 640L, Frames.FirstOrDefault()?.Sequence ?? 0, Frames.LastOrDefault()?.Sequence ?? 0, 0, 0, Frames.FirstOrDefault()?.CapturedAtUtc.ToUnixTimeMilliseconds() ?? 0, Frames.LastOrDefault()?.CapturedAtUtc.ToUnixTimeMilliseconds() ?? 0)); }
        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ShutdownAsync(Guid workerSessionId, CancellationToken cancellationToken = default) { Events.Add("shutdown"); owner.Processes[^1].Exit(0); return Task.FromResult(true); }
        public Task StopAsync() => Task.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
