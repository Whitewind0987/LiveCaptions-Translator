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
    public async Task UnexpectedExitBecomesTypedFailureWithoutAutomaticRestart()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create(); await supervisor.StartAsync(Token);
        fixture.Launcher.Processes[0].Exit(23);
        await WaitAsync(() => supervisor.State == AsrWorkerState.Faulted);
        Assert.Equal(AsrWorkerFailureKind.WorkerExited, supervisor.Diagnostics.FailureKind);
        Assert.Equal(1, fixture.Launcher.LaunchCount);
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
        Assert.Equal(stoppedCount, count); Assert.True(count >= 4);
    }

    [Fact]
    public async Task JobAssignmentResultIsRetained()
    {
        var fixture = new Fixture(); await using var supervisor = fixture.Create(); await supervisor.StartAsync(Token);
        Assert.True(supervisor.Diagnostics.JobAssignmentSucceeded);
    }

    private sealed class Fixture
    {
        public FakeLauncher Launcher { get; } = new(); public FakeTransportFactory TransportFactory { get; }
        public Fixture() { TransportFactory = new FakeTransportFactory(Launcher); }
        public AsrWorkerSupervisor Create(string? path = null) => new(path ?? ExistingPath(), Launcher, new FakeJobFactory(), TransportFactory, TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(100));
        private static string ExistingPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "AGENTS.md"));
    }

    private sealed class FakeLauncher : IWorkerProcessLauncher
    {
        public int LaunchCount { get; private set; } public List<FakeProcess> Processes { get; } = [];
        public Task<IWorkerProcess> LaunchAsync(WorkerLaunchRequest request, CancellationToken cancellationToken = default) { var process = new FakeProcess(1000 + ++LaunchCount); Processes.Add(process); return Task.FromResult<IWorkerProcess>(process); }
    }
    private sealed class FakeProcess(int id) : IWorkerProcess
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously); private int? exitCode;
        public int Id => id; public nint NativeHandle => 1; public bool HasExited => completion.Task.IsCompleted; public int? ExitCode => exitCode; public Task Completion => completion.Task; public IReadOnlyList<string> RecentStdout => []; public IReadOnlyList<string> RecentStderr => [];
        public void Exit(int code) { exitCode = code; completion.TrySetResult(); }
        public Task TerminateTreeAsync(CancellationToken cancellationToken = default) { Exit(-1); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { if (!HasExited) Exit(0); return ValueTask.CompletedTask; }
    }
    private sealed class FakeJobFactory : IWorkerJobFactory { public IWorkerJob Create() => new FakeJob(); }
    private sealed class FakeJob : IWorkerJob { public bool AssignmentSucceeded { get; private set; } public string? FailureReason => null; public Task AssignAsync(IWorkerProcess process, CancellationToken cancellationToken = default) { AssignmentSucceeded = true; return Task.CompletedTask; } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class FakeTransportFactory(FakeLauncher launcher) : IAsrWorkerTransportFactory
    {
        public List<WorkerPipeIdentity> Identities { get; } = [];
        public IAsrWorkerTransport Create(WorkerPipeIdentity identity) { Identities.Add(identity); return new FakeTransport(launcher); }
    }
    private sealed class FakeTransport(FakeLauncher launcher) : IAsrWorkerTransport
    {
        public event EventHandler<AudioStreamSummaryPayload>? ProgressReceived { add { } remove { } } public event EventHandler<ErrorPayload>? ErrorReceived { add { } remove { } }
        public long ControlMessagesSent => 0; public long ControlMessagesReceived => 0; public DateTimeOffset? LatestPongAtUtc => DateTimeOffset.UtcNow;
        public Task<WorkerTransportStartResult> ConnectAndHandshakeAsync(int expectedPid, CancellationToken cancellationToken = default) => Task.FromResult(new WorkerTransportStartResult(0, WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink, TimeSpan.Zero));
        public Task StartAudioStreamAsync(StartAudioStreamPayload request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAudioFrameAsync(Guid workerSessionId, NormalizedAudioFrame frame, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AudioStreamSummaryPayload> StopAudioStreamAsync(Guid workerSessionId, Guid captureSessionId, CancellationToken cancellationToken = default) => Task.FromResult(new AudioStreamSummaryPayload(captureSessionId,0,0,0,0,0,0,0,0));
        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ShutdownAsync(Guid workerSessionId, CancellationToken cancellationToken = default) { launcher.Processes[^1].Exit(0); return Task.FromResult(true); }
        public Task StopAsync() => Task.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task WaitAsync(Func<bool> condition) { for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(5, Token); Assert.True(condition()); }
}
