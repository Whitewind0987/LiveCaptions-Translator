using LiveCaptionsTranslator.captioning;
using LiveCaptionsTranslator.captioning.local;
using LiveCaptionsTranslator.models;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class CaptionSourceSelectionControllerTests
{
    [Fact]
    public async Task StartupWindowsSelectsOnlyWindowsWithoutProvisioning()
    {
        var harness = new Harness();

        var result = await harness.Controller.StartConfiguredAsync(
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal([CaptionSourceKind.WindowsLiveCaptions], harness.Selections);
        Assert.Equal(0, harness.ProvisioningQueries);
    }

    [Fact]
    public async Task StartupLocalSelectsOnlyLocalAndFailureKeepsPreference()
    {
        var harness = new Harness(CaptionSourceKind.LocalAsr)
        {
            SelectBehavior = (_, _) => Task.FromResult(
                CaptionSourceStartResult.Failed(
                    CaptionSourceState.Unavailable,
                    "Local ASR runtime assets are unavailable."))
        };

        var result = await harness.Controller.StartConfiguredAsync(
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal([CaptionSourceKind.LocalAsr], harness.Selections);
        Assert.Equal(0, harness.ProvisioningQueries);
        Assert.Equal(CaptionSourceKind.LocalAsr, harness.Setting.CaptionSource);
    }

    [Fact]
    public async Task StartupWithUnknownPersistedValueSelectsWindows()
    {
        var path = TemporarySettingPath();
        File.WriteAllText(path, "{\"CaptionSource\":\"unsupported\"}");
        try
        {
            var harness = new Harness(Setting.Load(path));

            var result = await harness.Controller.StartConfiguredAsync(
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal([CaptionSourceKind.WindowsLiveCaptions], harness.Selections);
            Assert.Equal(0, harness.ProvisioningQueries);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidLocalPrevalidationLeavesActiveSourceAndPreferenceUntouched()
    {
        var harness = new Harness
        {
            ActiveSource = CaptionSourceKind.WindowsLiveCaptions,
            SourceState = CaptionSourceState.Running,
            Provisioning = ProvisioningUnavailable()
        };

        var result = await harness.Controller.SelectAsync(
            CaptionSourceKind.LocalAsr,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Empty(harness.Selections);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, harness.ActiveSource);
        Assert.Equal(CaptionSourceState.Running, harness.SourceState);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, harness.Setting.CaptionSource);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, result.Status.PersistedSource);
        Assert.Contains(@"asr\ggml-tiny.bin", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulLocalSelectionPersistsOnlyAfterCoordinatorSuccess()
    {
        var path = TemporarySettingPath();
        try
        {
            var setting = Setting.Load(path);
            var harness = new Harness(setting)
            {
                Provisioning = ProvisioningReady()
            };
            harness.SelectBehavior = (kind, _) =>
            {
                Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, setting.CaptionSource);
                Assert.False(File.Exists(path));
                harness.ActiveSource = kind;
                harness.SourceState = CaptionSourceState.Running;
                return Task.FromResult(CaptionSourceStartResult.Started(Guid.NewGuid()));
            };

            var result = await harness.Controller.SelectAsync(
                CaptionSourceKind.LocalAsr,
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal([CaptionSourceKind.LocalAsr], harness.Selections);
            Assert.Equal(CaptionSourceKind.LocalAsr, setting.CaptionSource);
            Assert.Contains("\"CaptionSource\": \"LocalAsr\"", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal(CaptionSourceKind.LocalAsr, result.Status.PersistedSource);
            Assert.Equal(CaptionSourceKind.LocalAsr, result.Status.ActiveSource);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SuccessfulWindowsSelectionNeverQueriesLocalAndPersistsAfterSuccess()
    {
        var harness = new Harness(CaptionSourceKind.LocalAsr);
        harness.SelectBehavior = (kind, _) =>
        {
            Assert.Equal(CaptionSourceKind.LocalAsr, harness.Setting.CaptionSource);
            harness.ActiveSource = kind;
            harness.SourceState = CaptionSourceState.Running;
            return Task.FromResult(CaptionSourceStartResult.Started(Guid.NewGuid()));
        };

        var result = await harness.Controller.SelectAsync(
            CaptionSourceKind.WindowsLiveCaptions,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0, harness.ProvisioningQueries);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, harness.Setting.CaptionSource);
    }

    [Fact]
    public async Task CoordinatorFailureDoesNotPersistOrFallBack()
    {
        var harness = new Harness
        {
            Provisioning = ProvisioningReady(),
            SelectBehavior = (_, _) => Task.FromResult(
                CaptionSourceStartResult.Failed(CaptionSourceState.Faulted, "worker failed"))
        };

        var result = await harness.Controller.SelectAsync(
            CaptionSourceKind.LocalAsr,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(result.SourceSelectionSucceeded);
        Assert.False(result.PreferencePersisted);
        Assert.Equal("worker failed", result.FailureReason);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, harness.Setting.CaptionSource);
        Assert.Equal([CaptionSourceKind.LocalAsr], harness.Selections);
    }

    [Fact]
    public async Task SelectionFailureExposesRelativeWorkerPathWhileRawFailureIsUnchanged()
    {
        var layout = TestLayout();
        var raw = $"Worker executable '{layout.WorkerExecutablePath}' was not found.";
        var harness = new Harness
        {
            Provisioning = ProvisioningReady(),
            SelectBehavior = (_, _) => Task.FromResult(
                CaptionSourceStartResult.Failed(CaptionSourceState.Faulted, raw))
        };

        var result = await harness.Controller.SelectAsync(
            CaptionSourceKind.LocalAsr,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(@"asr\LiveCaptionsAsrWorker.exe", result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(layout.WorkerExecutablePath, result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(raw, harness.SelectFailureReasonObserved);
    }

    [Fact]
    public async Task PersistenceFailureKeepsActiveSourceAndRollsBackPreferenceUntilRetry()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"lct-stage64-persistence-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "setting.json");
        Directory.CreateDirectory(root);
        try
        {
            var setting = Setting.Load(path);
            Directory.CreateDirectory(path);
            var harness = new Harness(setting) { Provisioning = ProvisioningReady() };

            var failed = await harness.Controller.SelectAsync(
                CaptionSourceKind.LocalAsr,
                TestContext.Current.CancellationToken);

            Assert.False(failed.Success);
            Assert.True(failed.SourceSelectionSucceeded);
            Assert.False(failed.PreferencePersisted);
            Assert.Equal(CaptionSourceKind.LocalAsr, harness.ActiveSource);
            Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, setting.CaptionSource);
            Assert.Equal(CaptionSourceKind.LocalAsr, failed.Status.ActiveSource);
            Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, failed.Status.PersistedSource);
            Assert.Equal(
                "The caption source changed, but the preference could not be saved.",
                failed.FailureReason);
            Assert.Equal(failed.FailureReason, failed.Status.PreferencePersistenceFailureReason);
            Assert.DoesNotContain(root, failed.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal([CaptionSourceKind.LocalAsr], harness.Selections);

            Directory.Delete(path);
            var retried = await harness.Controller.SelectAsync(
                CaptionSourceKind.LocalAsr,
                TestContext.Current.CancellationToken);

            Assert.True(retried.Success);
            Assert.True(retried.SourceSelectionSucceeded);
            Assert.True(retried.PreferencePersisted);
            Assert.Null(retried.Status.PreferencePersistenceFailureReason);
            Assert.Equal(CaptionSourceKind.LocalAsr, setting.CaptionSource);
            Assert.Equal(
                [CaptionSourceKind.LocalAsr, CaptionSourceKind.LocalAsr],
                harness.Selections);
            Assert.Contains(
                "\"CaptionSource\": \"LocalAsr\"",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDoesNotPersistRequestedSource()
    {
        var harness = new Harness { Provisioning = ProvisioningReady() };
        using var cancellation = new CancellationTokenSource();
        harness.SelectBehavior = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<CaptionSourceStartResult>(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Controller.SelectAsync(CaptionSourceKind.LocalAsr, cancellation.Token));

        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, harness.Setting.CaptionSource);
    }

    [Fact]
    public async Task ConcurrentSelectionsDoNotOverlapCoordinatorTransitions()
    {
        var harness = new Harness { Provisioning = ProvisioningReady() };
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCalls = 0;
        var maximumCalls = 0;
        harness.SelectBehavior = async (kind, token) =>
        {
            var current = Interlocked.Increment(ref activeCalls);
            maximumCalls = Math.Max(maximumCalls, current);
            if (kind == CaptionSourceKind.LocalAsr)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            harness.ActiveSource = kind;
            harness.SourceState = CaptionSourceState.Running;
            Interlocked.Decrement(ref activeCalls);
            return CaptionSourceStartResult.Started(Guid.NewGuid());
        };

        var testCancellation = TestContext.Current.CancellationToken;
        var first = harness.Controller.SelectAsync(CaptionSourceKind.LocalAsr, testCancellation);
        await firstEntered.Task.WaitAsync(testCancellation);
        var second = harness.Controller.SelectAsync(
            CaptionSourceKind.WindowsLiveCaptions,
            testCancellation);
        await Task.Yield();

        Assert.Single(harness.Selections);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maximumCalls);
        Assert.Equal(
            [CaptionSourceKind.LocalAsr, CaptionSourceKind.WindowsLiveCaptions],
            harness.Selections);
    }

    [Fact]
    public async Task RepeatedSelectionOfActivePersistedSourceIsIdempotent()
    {
        var source = new IdempotentCaptionSource();
        await using var coordinator = new CaptionSourceCoordinator(() => source);
        var setting = new Setting();
        var controller = new CaptionSourceSelectionController(
            setting,
            coordinator.SelectAsync,
            ProvisioningReady,
            () => coordinator.CurrentSource,
            () => coordinator.State,
            () => coordinator.FailureReason,
            TestSanitizer());
        var cancellationToken = TestContext.Current.CancellationToken;

        var startup = await controller.StartConfiguredAsync(cancellationToken);
        var repeated = await controller.SelectAsync(
            CaptionSourceKind.WindowsLiveCaptions,
            cancellationToken);

        Assert.True(startup.Success);
        Assert.True(repeated.Success);
        Assert.Equal(1, source.StartCount);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, setting.CaptionSource);
    }

    [Fact]
    public async Task RetryCanSucceedAfterProvisioningChangesToReady()
    {
        var harness = new Harness { Provisioning = ProvisioningUnavailable() };

        var rejected = await harness.Controller.SelectAsync(
            CaptionSourceKind.LocalAsr,
            TestContext.Current.CancellationToken);
        harness.Provisioning = ProvisioningReady();
        var accepted = await harness.Controller.SelectAsync(
            CaptionSourceKind.LocalAsr,
            TestContext.Current.CancellationToken);

        Assert.False(rejected.Success);
        Assert.True(accepted.Success);
        Assert.Equal(2, harness.ProvisioningQueries);
        Assert.Single(harness.Selections);
        Assert.Equal(CaptionSourceKind.LocalAsr, harness.Setting.CaptionSource);
    }

    [Fact]
    public async Task RefreshPerformsFreshQueriesWithoutSelectingOrPersisting()
    {
        var harness = new Harness { Provisioning = ProvisioningUnavailable() };
        Assert.Equal(LocalAsrProvisioningState.Unknown, harness.Controller.Status.LocalAsrProvisioning);

        await harness.Controller.RefreshLocalAsrProvisioningAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(LocalAsrProvisioningState.Unavailable, harness.Controller.Status.LocalAsrProvisioning);
        harness.Provisioning = ProvisioningReady();
        await harness.Controller.RefreshLocalAsrProvisioningAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, harness.ProvisioningQueries);
        Assert.Empty(harness.Selections);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, harness.Setting.CaptionSource);
        Assert.Equal(LocalAsrProvisioningState.Ready, harness.Controller.Status.LocalAsrProvisioning);
    }

    [Fact]
    public async Task BusyAndFailureStatusUseSafeUiText()
    {
        var harness = new Harness { Provisioning = ProvisioningUnavailable() };
        var statuses = new List<CaptionSourceApplicationStatus>();
        harness.Controller.StatusChanged += (_, status) => statuses.Add(status);

        var result = await harness.Controller.SelectAsync(
            CaptionSourceKind.LocalAsr,
            TestContext.Current.CancellationToken);

        Assert.Contains(statuses, status => status.IsSelectionInProgress);
        Assert.False(result.Status.IsSelectionInProgress);
        Assert.Contains(@"asr\ggml-tiny.bin", result.Status.LocalAsrProvisioningFailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\developer", result.Status.LocalAsrProvisioningFailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProvisioningAndSelectionFailuresRemoveUnrelatedUncPath()
    {
        var harness = new Harness
        {
            Provisioning = ProvisioningUnavailable(
                @"Model inspection failed at \\developer-host\private-share\model.bin.")
        };

        var result = await harness.Controller.SelectAsync(
            CaptionSourceKind.LocalAsr,
            TestContext.Current.CancellationToken);

        var failure = harness.Controller.Status.LocalAsrProvisioningFailureReason;
        Assert.NotNull(failure);
        Assert.DoesNotContain(@"\\developer-host\private-share", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[path]", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\developer-host\private-share", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[path]", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownWorkerPathBecomesStableRelativePathWithoutChangingRawFailure()
    {
        var layout = TestLayout();
        var sanitizer = new CaptionSourceUiFailureSanitizer(layout);
        var raw = $"Worker executable '{layout.WorkerExecutablePath}' was not found.";
        var harness = new Harness
        {
            SourceState = CaptionSourceState.Faulted,
            SourceFailure = raw
        };

        var sanitized = sanitizer.Sanitize(raw);
        var statusFailure = harness.Controller.Status.SourceFailureReason;

        Assert.Contains(@"asr\LiveCaptionsAsrWorker.exe", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(layout.WorkerExecutablePath, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"asr\LiveCaptionsAsrWorker.exe", statusFailure, StringComparison.Ordinal);
        Assert.Equal(raw, harness.SourceFailure);
    }

    [Fact]
    public void KnownModelPathBecomesStableRelativePath()
    {
        var layout = TestLayout();
        var sanitizer = new CaptionSourceUiFailureSanitizer(layout);

        var sanitized = sanitizer.Sanitize(
            $"Model '{layout.WhisperModelPath}' could not be opened.");

        Assert.Contains(@"asr\ggml-tiny.bin", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(layout.WhisperModelPath, sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"Failure at D:\private\worker.exe.")]
    [InlineData(@"Failure at \\developer-host\private-share\worker.exe.")]
    public void UnrelatedAbsolutePathsCannotEnterUiFailure(string raw)
    {
        var sanitized = TestSanitizer().Sanitize(raw);

        Assert.NotNull(sanitized);
        Assert.DoesNotMatch(@"[A-Za-z]:[\\/]", sanitized);
        Assert.DoesNotMatch(@"(?:\\\\|//)[^\\/\s]+[\\/][^\\/\s]+", sanitized);
    }

    [Fact]
    public void PathFreeFailureRemainsReadable()
    {
        const string failure = "The worker stopped unexpectedly.";

        Assert.Equal(failure, TestSanitizer().Sanitize(failure));
    }

    [Fact]
    public void RuntimeFailureNotificationUpdatesStatusWithoutChangingPreference()
    {
        var harness = new Harness(CaptionSourceKind.LocalAsr)
        {
            ActiveSource = CaptionSourceKind.LocalAsr,
            SourceState = CaptionSourceState.Faulted,
            SourceFailure = "recognition stopped"
        };
        CaptionSourceApplicationStatus? observed = null;
        harness.Controller.StatusChanged += (_, status) => observed = status;

        harness.Controller.NotifySourceStatusChanged();

        Assert.NotNull(observed);
        Assert.Equal(CaptionSourceState.Faulted, observed.SourceState);
        Assert.Equal("recognition stopped", observed.SourceFailureReason);
        Assert.Equal(CaptionSourceKind.LocalAsr, harness.Setting.CaptionSource);
    }

    [Fact]
    public async Task DisposedSettingsSessionIgnoresLateAsyncCompletion()
    {
        var harness = new Harness { Provisioning = ProvisioningReady() };
        var selectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSelection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.SelectBehavior = async (kind, token) =>
        {
            selectionEntered.TrySetResult();
            await releaseSelection.Task.WaitAsync(token);
            harness.ActiveSource = kind;
            harness.SourceState = CaptionSourceState.Running;
            return CaptionSourceStartResult.Started(Guid.NewGuid());
        };
        var session = new CaptionSourceSettingsSession(harness.Controller);
        var notifications = 0;
        session.StatusChanged += (_, _) => notifications++;

        var testCancellation = TestContext.Current.CancellationToken;
        var selection = session.SelectAsync(CaptionSourceKind.LocalAsr, testCancellation);
        await selectionEntered.Task.WaitAsync(testCancellation);
        session.Dispose();
        var beforeCompletion = notifications;
        releaseSelection.TrySetResult();

        var result = await selection;

        Assert.True(result.Success);
        Assert.Equal(beforeCompletion, notifications);
    }

    private static LocalAsrProvisioningResult ProvisioningReady() =>
        Provisioning(valid: true);

    private static LocalAsrProvisioningResult ProvisioningUnavailable(
        string? failureReason = null) =>
        Provisioning(valid: false, failureReason: failureReason);

    private static LocalAsrProvisioningResult Provisioning(
        bool valid,
        string? failureReason = null)
    {
        LocalAsrAssetStatus Asset(
            LocalAsrAssetIdentity identity,
            string relativePath,
            bool assetValid = true) =>
            new(
                identity,
                relativePath,
                $@"C:\developer\checkout\{relativePath}",
                assetValid,
                true,
                1,
                identity == LocalAsrAssetIdentity.WorkerExecutable ? null : 1,
                null,
                assetValid,
                assetValid
                    ? null
                    : failureReason ?? $"{relativePath} is missing or invalid.");

        return new LocalAsrProvisioningResult(
        [
            Asset(LocalAsrAssetIdentity.WorkerExecutable, @"asr\LiveCaptionsAsrWorker.exe"),
            Asset(LocalAsrAssetIdentity.OnnxRuntime, @"asr\onnxruntime.dll"),
            Asset(LocalAsrAssetIdentity.SileroVadModel, @"asr\silero_vad_16k_op15.onnx"),
            Asset(LocalAsrAssetIdentity.WhisperModel, @"asr\ggml-tiny.bin", valid)
        ]);
    }

    private static string TemporarySettingPath() =>
        Path.Combine(Path.GetTempPath(), $"lct-stage64-selection-{Guid.NewGuid():N}.json");

    private static LocalAsrRuntimeLayout TestLayout() =>
        new(Path.Combine(Path.GetTempPath(), "lct-stage64-application"));

    private static CaptionSourceUiFailureSanitizer TestSanitizer() =>
        new(TestLayout());

    private sealed class Harness
    {
        private Func<CaptionSourceKind, CancellationToken, Task<CaptionSourceStartResult>>
            selectBehavior;

        internal Harness(CaptionSourceKind persistedSource = CaptionSourceKind.WindowsLiveCaptions)
            : this(new Setting { CaptionSource = persistedSource })
        {
        }

        internal Harness(Setting setting)
        {
            Setting = setting;
            selectBehavior = DefaultSelectAsync;
            Controller = new CaptionSourceSelectionController(
                setting,
                SelectAsync,
                Validate,
                () => ActiveSource,
                () => SourceState,
                () => SourceFailure,
                TestSanitizer());
        }

        internal Setting Setting { get; }
        internal CaptionSourceSelectionController Controller { get; }
        internal List<CaptionSourceKind> Selections { get; } = [];
        internal CaptionSourceKind? ActiveSource { get; set; }
        internal CaptionSourceState SourceState { get; set; } = CaptionSourceState.Stopped;
        internal string? SourceFailure { get; set; }
        internal LocalAsrProvisioningResult Provisioning { get; set; } = ProvisioningReady();
        internal int ProvisioningQueries { get; private set; }
        internal string? SelectFailureReasonObserved { get; private set; }

        internal Func<CaptionSourceKind, CancellationToken, Task<CaptionSourceStartResult>>
            SelectBehavior
        {
            get => selectBehavior;
            set => selectBehavior = value;
        }

        private Task<CaptionSourceStartResult> SelectAsync(
            CaptionSourceKind kind,
            CancellationToken cancellationToken)
        {
            Selections.Add(kind);
            return ObserveFailureAsync(selectBehavior(kind, cancellationToken));
        }

        private async Task<CaptionSourceStartResult> ObserveFailureAsync(
            Task<CaptionSourceStartResult> operation)
        {
            var result = await operation;
            SelectFailureReasonObserved = result.FailureReason;
            return result;
        }

        private Task<CaptionSourceStartResult> DefaultSelectAsync(
            CaptionSourceKind kind,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveSource = kind;
            SourceState = CaptionSourceState.Running;
            SourceFailure = null;
            return Task.FromResult(CaptionSourceStartResult.Started(Guid.NewGuid()));
        }

        private LocalAsrProvisioningResult Validate()
        {
            ProvisioningQueries++;
            return Provisioning;
        }
    }

    private sealed class IdempotentCaptionSource : ICaptionSource
    {
        private readonly Guid sessionId = Guid.NewGuid();

        public string SourceId => "stage-6.4-idempotent";
        public CaptionSourceState State { get; private set; } = CaptionSourceState.Stopped;
        public string? FailureReason => null;
        public int StartCount { get; private set; }

        public event EventHandler<CaptionEvent>? CaptionEventReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<CaptionSourceStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task<CaptionSourceStartResult> StartAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            State = CaptionSourceState.Running;
            return Task.FromResult(CaptionSourceStartResult.Started(sessionId));
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = CaptionSourceState.Stopped;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = CaptionSourceState.Stopped;
            return ValueTask.CompletedTask;
        }
    }
}
