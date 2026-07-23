using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.ipc;
using LiveCaptionsTranslator.worker;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class AsrWorkerSupervisorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartLaunchesOneProcessAndDuplicateStartIsIdempotent()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create();
        var first = await supervisor.StartAsync(Token); var second = await supervisor.StartAsync(Token);
        Assert.True(first.Success); Assert.Equal(first.SessionId, second.SessionId); Assert.Equal(1, fixture.Launcher.LaunchCount);
        await supervisor.StopAsync(Token); Assert.Equal(AsrWorkerState.Stopped, supervisor.State);
    }

    [Fact]
    public async Task ExplicitRestartCreatesNewSessionAndOneNewProcess()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create();
        var first = await supervisor.StartAsync(Token); var second = await supervisor.RestartAsync(Token);
        Assert.True(second.Success); Assert.NotEqual(first.SessionId, second.SessionId); Assert.Equal(2, fixture.Launcher.LaunchCount);
        Assert.Equal(2, fixture.TransportFactory.Identities.DistinctBy(value => value.ControlPipeName).Count());
    }

    [Fact]
    public async Task UnexpectedExitImmediatelyRunsEveryCleanupPhase()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create(); await supervisor.StartAsync(Token);
        fixture.Launcher.Processes[0].Exit(23);
        await WaitAsync(() => supervisor.State == AsrWorkerState.Faulted && fixture.TransportFactory.Transports[0].DisposeCount == 1);
        Assert.Equal(AsrWorkerFailureKind.WorkerExited, supervisor.Diagnostics.FailureKind);
        Assert.Equal(1, fixture.TransportFactory.Transports[0].StopCount);
        Assert.Equal(1, fixture.JobFactory.Jobs[0].DisposeCount);
        Assert.Equal(1, fixture.Launcher.Processes[0].DisposeCount);
        Assert.Equal(1, fixture.Launcher.LaunchCount);
    }

    [Fact]
    public async Task HeartbeatFailureImmediatelyCleansOwnedWorker()
    {
        var fixture = new Fixture { FailPing = true };
        await using var supervisor = fixture.Create(heartbeatInterval: TimeSpan.FromMilliseconds(5), heartbeatTimeout: TimeSpan.FromMilliseconds(20));
        await supervisor.StartAsync(Token);
        await WaitAsync(() => supervisor.State == AsrWorkerState.Faulted && fixture.TransportFactory.Transports[0].DisposeCount == 1);
        Assert.Equal(AsrWorkerFailureKind.HeartbeatTimeout, supervisor.Diagnostics.FailureKind);
        Assert.True(supervisor.Diagnostics.HeartbeatFailures > 0);
    }

    [Fact]
    public async Task TransportTerminalFailureImmediatelyUsesTypedCleanup()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create(); await supervisor.StartAsync(Token);
        fixture.TransportFactory.Transports[0].Fail(new(AsrWorkerFailureKind.ControlPipeClosed, "closed"));
        await WaitAsync(() => supervisor.State == AsrWorkerState.Faulted && fixture.TransportFactory.Transports[0].DisposeCount == 1);
        Assert.Equal(AsrWorkerFailureKind.ControlPipeClosed, supervisor.Diagnostics.FailureKind);
        Assert.Equal(AsrWorkerFailureKind.ControlPipeClosed, supervisor.Diagnostics.TransportFailureKind);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("transport")]
    [InlineData("heartbeat")]
    public async Task SynchronouslyCompletedMonitorOriginIsJoinedByCoordinator(string origin)
    {
        var fixture = new Fixture
        {
            ProcessInitiallyExited = origin == "process",
            TransportInitiallyFailed = origin == "transport",
            FailPing = origin == "heartbeat"
        };
        await using var supervisor = fixture.Create(heartbeatInterval: origin == "heartbeat" ? TimeSpan.Zero : TimeSpan.FromHours(1));
        await supervisor.StartAsync(Token);
        await WaitAsync(() => supervisor.State == AsrWorkerState.Faulted && fixture.TransportFactory.Transports[0].DisposeCount == 1);
        Assert.Equal(1, fixture.TransportFactory.Transports[0].StopCount);
        Assert.Equal(1, fixture.Launcher.Processes[0].DisposeCount);
    }

    [Fact]
    public async Task ProgressPopulatesSupervisorDiagnostics()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create(); await supervisor.StartAsync(Token);
        var capture = Guid.NewGuid();
        fixture.TransportFactory.Transports[0].Report(new(capture, 50, 32000, 1, 50, 2, 0, 1000, 2000));
        var diagnostics = supervisor.Diagnostics;
        Assert.Equal(capture, diagnostics.ActiveCaptureSessionId); Assert.Equal(50, diagnostics.WorkerFramesReceived); Assert.Equal(32000, diagnostics.WorkerBytesReceived); Assert.Equal(2, diagnostics.SequenceGaps); Assert.NotNull(diagnostics.LatestProgressAtUtc);
    }

    [Theory]
    [InlineData(AsrWorkerState.Starting)]
    [InlineData(AsrWorkerState.Ready)]
    [InlineData(AsrWorkerState.Streaming)]
    [InlineData(AsrWorkerState.Faulted)]
    [InlineData(AsrWorkerState.Stopping)]
    [InlineData(AsrWorkerState.Stopped)]
    public async Task SynchronousStopFromEveryPublishedTransitionDoesNotDeadlock(AsrWorkerState transition)
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create();
        var invoked = 0;
        supervisor.StatusChanged += (_, status) =>
        {
            if (status.State != transition || Interlocked.Exchange(ref invoked, 1) != 0) return;
            supervisor.StopAsync(Token).GetAwaiter().GetResult();
        };

        var start = await supervisor.StartAsync(Token);
        if (transition == AsrWorkerState.Streaming && start.Success) await supervisor.SetStreamingAsync(true, Token);
        else if (transition == AsrWorkerState.Faulted && start.Success) fixture.Launcher.Processes[^1].Exit(7);
        else if ((transition is AsrWorkerState.Stopping or AsrWorkerState.Stopped) && start.Success) await supervisor.StopAsync(Token);
        await WaitAsync(() => invoked == 1);
        await WaitAsync(() => supervisor.State == AsrWorkerState.Stopped);
        Assert.Equal(1, invoked);
    }

    [Fact]
    public async Task MissingExecutableIsTypedUnavailable()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"));
        var result = await supervisor.StartAsync(Token);
        Assert.False(result.Success); Assert.Equal(AsrWorkerFailureKind.WorkerExecutableMissing, result.FailureKind); Assert.Equal(0, fixture.Launcher.LaunchCount);
    }

    [Fact]
    public async Task SubscriberExceptionsAreIsolatedAndNoEventFollowsStop()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create(); var count = 0;
        supervisor.StatusChanged += (_, _) => throw new InvalidOperationException(); supervisor.StatusChanged += (_, _) => count++;
        await supervisor.StartAsync(Token); await supervisor.StopAsync(Token); var stoppedCount = count; await Task.Yield();
        Assert.Equal(stoppedCount, count); Assert.True(count >= 3);
    }

    [Fact]
    public async Task ReentrantReadyStopPreventsLaterSubscriberFromReceivingStaleReady()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create(); var staleReady = 0;
        supervisor.StatusChanged += (_, status) => { if (status.State == AsrWorkerState.Ready) supervisor.StopAsync(Token).GetAwaiter().GetResult(); };
        supervisor.StatusChanged += (_, status) => { if (status.State == AsrWorkerState.Ready) Interlocked.Increment(ref staleReady); };
        await supervisor.StartAsync(Token);
        await WaitAsync(() => supervisor.State == AsrWorkerState.Stopped);
        Assert.Equal(0, staleReady);
    }

    [Fact]
    public async Task RepeatedDisposeJoinsCleanupAndIsSafe()
    {
        var fixture = new Fixture(); var supervisor = fixture.Create(); await supervisor.StartAsync(Token);
        await supervisor.DisposeAsync();
        await supervisor.DisposeAsync();
        Assert.Equal(1, fixture.TransportFactory.Transports[0].DisposeCount);
    }

    [Fact]
    public async Task JobAssignmentResultIsRetained()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create(); await supervisor.StartAsync(Token);
        Assert.True(supervisor.Diagnostics.JobAssignmentSucceeded);
    }

    private sealed class Fixture
    {
        public bool FailPing { get; init; }
        public bool ProcessInitiallyExited { get; init; }
        public bool TransportInitiallyFailed { get; init; }
        public FakeLauncher Launcher { get; }
        public FakeJobFactory JobFactory { get; } = new();
        public FakeTransportFactory TransportFactory { get; }
        public Fixture() { Launcher = new FakeLauncher(this); TransportFactory = new FakeTransportFactory(Launcher, this); }
        public AsrWorkerSupervisor Create(string? path = null, TimeSpan? heartbeatInterval = null, TimeSpan? heartbeatTimeout = null) => new(path ?? ExistingPath(), Launcher, JobFactory, TransportFactory, heartbeatInterval ?? TimeSpan.FromHours(1), heartbeatTimeout ?? TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(50));
        private static string ExistingPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "AGENTS.md"));
    }

    private sealed class FakeLauncher(Fixture fixture) : IWorkerProcessLauncher
    {
        public int LaunchCount { get; private set; } public List<FakeProcess> Processes { get; } = [];
        public Task<IWorkerProcess> LaunchAsync(WorkerLaunchRequest request, CancellationToken cancellationToken = default) { var process = new FakeProcess(1000 + ++LaunchCount); Processes.Add(process); if (fixture.ProcessInitiallyExited) process.Exit(31); return Task.FromResult<IWorkerProcess>(process); }
    }

    private sealed class FakeProcess(int id) : IWorkerProcess
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously); private int? exitCode;
        public int Id => id; public nint NativeHandle => 1; public bool HasExited => completion.Task.IsCompleted; public int? ExitCode => exitCode; public Task Completion => completion.Task; public IReadOnlyList<string> RecentStdout => []; public IReadOnlyList<string> RecentStderr => []; public int DisposeCount { get; private set; } public int TerminateCount { get; private set; }
        public void Exit(int code) { exitCode = code; completion.TrySetResult(); }
        public Task TerminateTreeAsync(CancellationToken cancellationToken = default) { TerminateCount++; Exit(-1); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; if (!HasExited) Exit(0); return ValueTask.CompletedTask; }
    }

    private sealed class FakeJobFactory : IWorkerJobFactory
    {
        public List<FakeJob> Jobs { get; } = [];
        public IWorkerJob Create() { var job = new FakeJob(); Jobs.Add(job); return job; }
    }

    private sealed class FakeJob : IWorkerJob
    {
        public bool AssignmentSucceeded { get; private set; } public string? FailureReason => null; public int DisposeCount { get; private set; }
        public Task AssignAsync(IWorkerProcess process, CancellationToken cancellationToken = default) { AssignmentSucceeded = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class FakeTransportFactory(FakeLauncher launcher, Fixture fixture) : IAsrWorkerTransportFactory
    {
        public List<WorkerPipeIdentity> Identities { get; } = []; public List<FakeTransport> Transports { get; } = [];
        public IAsrWorkerTransport Create(WorkerPipeIdentity identity) { Identities.Add(identity); var transport = new FakeTransport(launcher, fixture); Transports.Add(transport); return transport; }
    }

    private sealed class FakeTransport(FakeLauncher launcher, Fixture fixture) : IAsrWorkerTransport
    {
        private readonly TaskCompletionSource<WorkerTransportFailure> terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<AudioStreamSummaryPayload>? ProgressReceived; public event EventHandler<ErrorPayload>? ErrorReceived;
        public long ControlMessagesSent => 2; public long ControlMessagesReceived => 2; public long AudioFramesSent => 3; public long AudioBytesSent => 1920; public DateTimeOffset? LatestPongAtUtc => DateTimeOffset.UtcNow; public Task<WorkerTransportFailure> TerminalFailure => terminal.Task; public int StopCount { get; private set; } public int DisposeCount { get; private set; }
        public Task<WorkerTransportStartResult> ConnectAndHandshakeAsync(int expectedPid, CancellationToken cancellationToken = default) { if (fixture.TransportInitiallyFailed) terminal.TrySetResult(new(AsrWorkerFailureKind.ProtocolViolation, "precompleted transport failure")); return Task.FromResult(new WorkerTransportStartResult(0, WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink, TimeSpan.Zero)); }
        public Task StartAudioStreamAsync(StartAudioStreamPayload request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAudioFrameAsync(Guid workerSessionId, NormalizedAudioFrame frame, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EndAudioStreamAsync(AudioStreamEndPayload end, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AudioStreamSummaryPayload> StopAudioStreamAsync(Guid workerSessionId, Guid captureSessionId, CancellationToken cancellationToken = default) => Task.FromResult(new AudioStreamSummaryPayload(captureSessionId, 0, 0, 0, 0, 0, 0, 0, 0));
        public Task PingAsync(CancellationToken cancellationToken = default) => fixture.FailPing ? Task.FromException(new TimeoutException("ping")) : Task.CompletedTask;
        public Task<bool> ShutdownAsync(Guid workerSessionId, CancellationToken cancellationToken = default) { launcher.Processes[^1].Exit(0); return Task.FromResult(true); }
        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
        public void Fail(WorkerTransportFailure failure) => terminal.TrySetResult(failure);
        public void Report(AudioStreamSummaryPayload progress) => ProgressReceived?.Invoke(this, progress);
        public void ReportError(ErrorPayload error) => ErrorReceived?.Invoke(this, error);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var index = 0; index < 200 && !condition(); index++) await Task.Delay(5, Token);
        Assert.True(condition());
    }
}
