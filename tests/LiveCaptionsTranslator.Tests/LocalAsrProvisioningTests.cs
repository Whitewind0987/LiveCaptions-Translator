using System.Diagnostics;
using System.Security.Cryptography;

using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.captioning;
using LiveCaptionsTranslator.captioning.local;
using LiveCaptionsTranslator.ipc;
using LiveCaptionsTranslator.worker;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

[CollectionDefinition(CurrentDirectoryCollection.Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "Local ASR current-directory tests";
}

[Collection(CurrentDirectoryCollection.Name)]
public sealed class LocalAsrProvisioningTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"lct-stage63-{Guid.NewGuid():N}");

    public LocalAsrProvisioningTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void FixedLayoutUsesInjectedApplicationBaseAndAuthoritativeNames()
    {
        var layout = new LocalAsrRuntimeLayout(directory);

        Assert.Equal(Path.GetFullPath(directory), layout.ApplicationBaseDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(directory), "asr"), layout.RuntimeRoot);
        Assert.Equal(
            Path.Combine(layout.RuntimeRoot, "LiveCaptionsAsrWorker.exe"),
            layout.WorkerExecutablePath);
        Assert.Equal(Path.Combine(layout.RuntimeRoot, "onnxruntime.dll"), layout.OnnxRuntimePath);
        Assert.Equal(
            Path.Combine(layout.RuntimeRoot, "silero_vad_16k_op15.onnx"),
            layout.SileroVadModelPath);
        Assert.Equal(Path.Combine(layout.RuntimeRoot, "ggml-tiny.bin"), layout.WhisperModelPath);
    }

    [Fact]
    public void FixedLayoutIsIndependentOfProcessCurrentDirectory()
    {
        var original = Environment.CurrentDirectory;
        var other = Path.Combine(directory, "other");
        Directory.CreateDirectory(other);
        try
        {
            Environment.CurrentDirectory = other;
            var layout = new LocalAsrRuntimeLayout(directory);

            Assert.Equal(Path.Combine(Path.GetFullPath(directory), "asr"), layout.RuntimeRoot);
        }
        finally
        {
            Environment.CurrentDirectory = original;
        }
    }

    [Fact]
    public void EveryAssetPathIsCanonicalAbsoluteAndInsideRuntimeRoot()
    {
        var layout = new LocalAsrRuntimeLayout(directory);
        var paths = new[]
        {
            layout.WorkerExecutablePath,
            layout.OnnxRuntimePath,
            layout.SileroVadModelPath,
            layout.WhisperModelPath
        };

        Assert.All(paths, path =>
        {
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.Equal(Path.GetFullPath(path), path);
            Assert.True(layout.Contains(path));
        });
    }

    [Theory]
    [InlineData(@"..\silero_vad_16k_op15.onnx")]
    [InlineData("silero_vad.onnx")]
    [InlineData(@"subdir\ggml-tiny.bin")]
    [InlineData(@"C:\ggml-tiny.bin")]
    public void FixedLayoutRejectsSubstitutionTraversalAndLegacyNames(string fileName)
    {
        var layout = new LocalAsrRuntimeLayout(directory);

        Assert.Throws<ArgumentException>(() => layout.ResolveFixedFileName(fileName));
    }

    [Fact]
    public void FixedLayoutRejectsRelativeApplicationBase()
    {
        Assert.Throws<ArgumentException>(() => new LocalAsrRuntimeLayout("relative-base"));
    }

    [Fact]
    public void FourValidAssetsReportProvisionedWithStableIdentityAndMetadata()
    {
        var fixture = ProvisioningFixture.Valid(directory);

        var result = fixture.Provisioning.Validate();

        Assert.True(result.IsProvisioned);
        Assert.Null(result.FailureReason);
        Assert.Collection(
            result.Assets,
            asset => AssertAsset(asset, LocalAsrAssetIdentity.WorkerExecutable, @"asr\LiveCaptionsAsrWorker.exe"),
            asset => AssertAsset(asset, LocalAsrAssetIdentity.OnnxRuntime, @"asr\onnxruntime.dll"),
            asset => AssertAsset(asset, LocalAsrAssetIdentity.SileroVadModel, @"asr\silero_vad_16k_op15.onnx"),
            asset => AssertAsset(asset, LocalAsrAssetIdentity.WhisperModel, @"asr\ggml-tiny.bin"));
        Assert.Equal(LocalAsrProvisioning.OnnxRuntimeLength, result.OnnxRuntime.ExpectedLength);
        Assert.Equal(LocalAsrProvisioning.SileroVadModelLength, result.SileroVadModel.ExpectedLength);
        Assert.Equal(LocalAsrProvisioning.SileroVadModelSha256, result.SileroVadModel.ExpectedSha256);
        Assert.Equal(LocalAsrProvisioning.WhisperModelLength, result.WhisperModel.ExpectedLength);
        Assert.Equal(LocalAsrProvisioning.WhisperModelSha256, result.WhisperModel.ExpectedSha256);
        Assert.Null(result.WorkerExecutable.ExpectedLength);
        Assert.Null(result.OnnxRuntime.ExpectedSha256);
    }

    [Theory]
    [InlineData(LocalAsrAssetIdentity.WorkerExecutable, @"asr\LiveCaptionsAsrWorker.exe")]
    [InlineData(LocalAsrAssetIdentity.OnnxRuntime, @"asr\onnxruntime.dll")]
    [InlineData(LocalAsrAssetIdentity.SileroVadModel, @"asr\silero_vad_16k_op15.onnx")]
    [InlineData(LocalAsrAssetIdentity.WhisperModel, @"asr\ggml-tiny.bin")]
    public void EachMissingAssetIsReported(
        LocalAsrAssetIdentity identity,
        string relativePath)
    {
        var fixture = ProvisioningFixture.Valid(directory);
        fixture.Inspector.Set(identity, Missing());

        var result = fixture.Provisioning.Validate();

        Assert.False(result.IsProvisioned);
        var status = Assert.Single(result.Assets, asset => !asset.IsValid);
        Assert.Equal(identity, status.Identity);
        Assert.Contains(relativePath, result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(directory, result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultipleMissingAssetsAreAggregated()
    {
        var fixture = ProvisioningFixture.Missing(directory);

        var result = fixture.Provisioning.Validate();

        Assert.False(result.IsProvisioned);
        Assert.All(result.Assets, asset => Assert.False(asset.IsValid));
        Assert.Contains(@"asr\LiveCaptionsAsrWorker.exe", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains(@"asr\onnxruntime.dll", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains(@"asr\silero_vad_16k_op15.onnx", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains(@"asr\ggml-tiny.bin", result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(directory, result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectoryAtRequiredPathIsRejected()
    {
        var fixture = ProvisioningFixture.Valid(directory);
        fixture.Inspector.Set(
            LocalAsrAssetIdentity.WorkerExecutable,
            new LocalAsrFileInspection(true, true, null, null));

        var result = fixture.Provisioning.Validate();

        Assert.False(result.WorkerExecutable.IsValid);
        Assert.Contains("must be a file", result.WorkerExecutable.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroLengthFileIsRejected()
    {
        var fixture = ProvisioningFixture.Valid(directory);
        fixture.Inspector.Set(
            LocalAsrAssetIdentity.WorkerExecutable,
            new LocalAsrFileInspection(true, false, 0, null));

        var result = fixture.Provisioning.Validate();

        Assert.False(result.WorkerExecutable.IsValid);
        Assert.Contains("non-empty", result.WorkerExecutable.FailureReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LocalAsrAssetIdentity.OnnxRuntime)]
    [InlineData(LocalAsrAssetIdentity.SileroVadModel)]
    [InlineData(LocalAsrAssetIdentity.WhisperModel)]
    public void PinnedLengthMismatchIsRejectedWithRelativePath(LocalAsrAssetIdentity identity)
    {
        var fixture = ProvisioningFixture.Valid(directory);
        var valid = fixture.Inspector.Get(identity);
        fixture.Inspector.Set(identity, valid with { Length = valid.Length + 1 });

        var result = fixture.Provisioning.Validate();
        var status = result.Assets.Single(asset => asset.Identity == identity);

        Assert.False(status.IsValid);
        Assert.Contains(status.ApplicationRelativePath, status.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(directory, status.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(LocalAsrAssetIdentity.SileroVadModel)]
    [InlineData(LocalAsrAssetIdentity.WhisperModel)]
    public void PinnedHashMismatchIsRejectedWithRelativePath(LocalAsrAssetIdentity identity)
    {
        var fixture = ProvisioningFixture.Valid(directory);
        var valid = fixture.Inspector.Get(identity);
        fixture.Inspector.Set(identity, valid with { Sha256 = new string('0', 64) });

        var result = fixture.Provisioning.Validate();
        var status = result.Assets.Single(asset => asset.Identity == identity);

        Assert.False(status.IsValid);
        Assert.Contains("SHA-256", status.FailureReason, StringComparison.Ordinal);
        Assert.Contains(status.ApplicationRelativePath, status.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(directory, status.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationCreatesAndModifiesNothing()
    {
        var baseDirectory = Path.Combine(directory, "empty-base");
        Directory.CreateDirectory(baseDirectory);
        var fixture = ProvisioningFixture.Missing(baseDirectory);
        var before = Directory.GetFileSystemEntries(baseDirectory);

        var result = fixture.Provisioning.Validate();

        Assert.False(result.IsProvisioned);
        Assert.Equal(before, Directory.GetFileSystemEntries(baseDirectory));
        Assert.False(Directory.Exists(fixture.Layout.RuntimeRoot));
    }

    [Fact]
    public void ProductionInspectorHashesWithoutRetainingAFileHandle()
    {
        var path = Path.Combine(directory, "inspect.bin");
        var moved = Path.Combine(directory, "moved.bin");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(path, bytes);
        var expected = Convert.ToHexString(SHA256.HashData(bytes));

        var inspection = new ProductionLocalAsrFileInspector().Inspect(path, calculateSha256: true);

        Assert.True(inspection.Exists);
        Assert.False(inspection.IsDirectory);
        Assert.Equal(bytes.Length, inspection.Length);
        Assert.Equal(expected, inspection.Sha256);
        File.Move(path, moved);
        using (var stream = new FileStream(moved, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.WriteByte(6);
        File.Delete(moved);
        Assert.False(File.Exists(moved));
    }

    [Fact]
    public void ConstructionAndFactoryRegistrationAreLazy()
    {
        var fixture = ProvisioningFixture.Missing(directory);
        var windows = new SuccessfulCaptionSource("windows");

        using var coordinator = new AsyncDisposableScope(new CaptionSourceCoordinator(
            () => windows,
            fixture.Provisioning.CreateSource));

        Assert.Equal(0, fixture.Inspector.CallCount);
        Assert.Equal(0, fixture.PipelineFactoryCalls);
    }

    [Fact]
    public void InvalidProvisioningConstructsNoPipelineOrNativeOwnership()
    {
        var fixture = ProvisioningFixture.Missing(directory);

        var error = Assert.Throws<InvalidOperationException>(fixture.Provisioning.CreateSource);

        Assert.Equal(0, fixture.PipelineFactoryCalls);
        Assert.Contains(@"asr\LiveCaptionsAsrWorker.exe", error.Message, StringComparison.Ordinal);
        Assert.Contains(@"asr\ggml-tiny.bin", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(directory, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidFactoryReturnsIndependentSources()
    {
        var fixture = ProvisioningFixture.Valid(directory);

        await using var first = fixture.Provisioning.CreateSource();
        await using var second = fixture.Provisioning.CreateSource();

        Assert.IsType<LocalAsrCaptionSource>(first);
        Assert.IsType<LocalAsrCaptionSource>(second);
        Assert.NotSame(first, second);
        Assert.Equal(0, fixture.PipelineFactoryCalls);
    }

    [Fact]
    public async Task ProductionPipelineCompositionCreatesIndependentOwnershipGraphsAndFixedRecognition()
    {
        var layout = CreateLayout();
        var captures = new List<AudioCaptureService>();
        var supervisors = new List<AsrWorkerSupervisor>();
        var pipelines = new List<AudioWorkerPipeline>();
        var recognitionInputs = new List<(string Vad, string Whisper, string Language, int Threads)>();
        var workerPaths = new List<string>();

        ILocalAsrPipeline Create() => LocalAsrProvisioning.CreateProductionPipeline(
            layout,
            () =>
            {
                var capture = new AudioCaptureService(
                    new FakeAudioEndpointProvider(),
                    new FakeAudioCaptureRuntimeFactory());
                captures.Add(capture);
                return capture;
            },
            (workerPath, configuration) =>
            {
                workerPaths.Add(workerPath);
                var supervisor = new AsrWorkerSupervisor(workerPath);
                supervisors.Add(supervisor);
                return supervisor;
            },
            (capture, supervisor) =>
            {
                var pipeline = new AudioWorkerPipeline(capture, supervisor);
                pipelines.Add(pipeline);
                return pipeline;
            },
            (vadPath, whisperPath, language, threads) =>
            {
                recognitionInputs.Add((vadPath, whisperPath, language, threads));
                return WorkerRecognitionConfiguration.Disabled;
            });

        await using var first = Create();
        await using var second = Create();

        Assert.Equal(2, captures.Count);
        Assert.Equal(2, supervisors.Count);
        Assert.Equal(2, pipelines.Count);
        Assert.NotSame(captures[0], captures[1]);
        Assert.NotSame(supervisors[0], supervisors[1]);
        Assert.NotSame(pipelines[0], pipelines[1]);
        Assert.All(workerPaths, path => Assert.Equal(layout.WorkerExecutablePath, path));
        Assert.All(recognitionInputs, input =>
        {
            Assert.Equal(layout.SileroVadModelPath, input.Vad);
            Assert.Equal(layout.WhisperModelPath, input.Whisper);
            Assert.Equal("auto", input.Language);
            Assert.Equal(WorkerRecognitionConfiguration.DefaultThreadCount, input.Threads);
            Assert.StartsWith(layout.RuntimeRoot, input.Vad, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(layout.RuntimeRoot, input.Whisper, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task DefaultWindowsStartDoesNotInspectLocalAssets()
    {
        var fixture = ProvisioningFixture.Missing(directory);
        var windows = new SuccessfulCaptionSource("windows");
        await using var coordinator = new CaptionSourceCoordinator(
            () => windows,
            fixture.Provisioning.CreateSource);

        var result = await coordinator.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, CaptionSourceCoordinator.DefaultSource);
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, coordinator.CurrentSource);
        Assert.Equal(0, fixture.Inspector.CallCount);
        Assert.Equal(0, fixture.PipelineFactoryCalls);
    }

    [Fact]
    public async Task MissingLocalAssetsFailWithoutFallbackOrNativeWork()
    {
        var fixture = ProvisioningFixture.Missing(directory);
        var windows = new SuccessfulCaptionSource("windows");
        await using var coordinator = new CaptionSourceCoordinator(
            () => windows,
            fixture.Provisioning.CreateSource);
        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        var result = await coordinator.SelectAsync(
            CaptionSourceKind.LocalAsr,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(CaptionSourceState.Faulted, result.State);
        Assert.Null(coordinator.CurrentSource);
        Assert.Equal(1, windows.StartCount);
        Assert.Equal(1, windows.StopCount);
        Assert.Equal(1, windows.DisposeCount);
        Assert.Equal(0, fixture.PipelineFactoryCalls);
        Assert.Contains(@"asr\LiveCaptionsAsrWorker.exe", coordinator.FailureReason, StringComparison.Ordinal);
        Assert.Contains(@"asr\ggml-tiny.bin", coordinator.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalSelectionCanRetryAfterAssetsAreSupplied()
    {
        var fixture = ProvisioningFixture.Missing(directory);
        await using var coordinator = new CaptionSourceCoordinator(
            () => new SuccessfulCaptionSource("windows"),
            fixture.Provisioning.CreateSource);

        var failed = await coordinator.SelectAsync(
            CaptionSourceKind.LocalAsr,
            TestContext.Current.CancellationToken);
        fixture.Inspector.SetAllValid();
        var retried = await coordinator.SelectAsync(
            CaptionSourceKind.LocalAsr,
            TestContext.Current.CancellationToken);

        Assert.False(failed.Success);
        Assert.True(retried.Success);
        Assert.Equal(CaptionSourceKind.LocalAsr, coordinator.CurrentSource);
        Assert.Equal(1, fixture.PipelineFactoryCalls);
    }

    [Fact]
    public void WorkerStartInfoUsesCanonicalExecutableDirectoryAndPreservesArguments()
    {
        var layout = CreateLayout();
        var existingFile = typeof(LocalAsrProvisioningTests).Assembly.Location;
        var recognition = WorkerRecognitionConfiguration.Create(
            existingFile,
            existingFile,
            "auto",
            WorkerRecognitionConfiguration.DefaultThreadCount);
        var session = Guid.NewGuid();
        var nonce = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var request = new WorkerLaunchRequest(
            layout.WorkerExecutablePath,
            "control-pipe",
            "audio-pipe",
            session,
            4321,
            nonce,
            recognition);
        var currentDirectory = Environment.CurrentDirectory;

        ProcessStartInfo info = WorkerProcessLauncher.CreateStartInfo(request);

        Assert.Equal(layout.WorkerExecutablePath, info.FileName);
        Assert.Equal(layout.RuntimeRoot, info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(currentDirectory, Environment.CurrentDirectory);
        Assert.Equal(new[]
        {
            "--control-pipe", "control-pipe",
            "--audio-pipe", "audio-pipe",
            "--session", session.ToString("D"),
            "--parent-pid", "4321",
            "--vad-model", existingFile,
            "--whisper-model", existingFile,
            "--language", "auto",
            "--threads", WorkerRecognitionConfiguration.DefaultThreadCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        }, info.ArgumentList);
        Assert.Equal(Convert.ToHexString(nonce), info.Environment[IpcProtocol.NonceEnvironmentVariable]);
    }

    private LocalAsrRuntimeLayout CreateLayout()
    {
        var baseDirectory = Path.Combine(directory, $"layout-{Guid.NewGuid():N}");
        return new LocalAsrRuntimeLayout(baseDirectory);
    }

    private static void AssertAsset(
        LocalAsrAssetStatus asset,
        LocalAsrAssetIdentity identity,
        string relativePath)
    {
        Assert.Equal(identity, asset.Identity);
        Assert.Equal(relativePath, asset.ApplicationRelativePath);
        Assert.True(asset.IsValid);
        Assert.True(asset.Exists);
        Assert.True(asset.IsFile);
        Assert.True(Path.IsPathFullyQualified(asset.ExpectedPath));
    }

    private static LocalAsrFileInspection Missing() => new(false, false, null, null);

    private sealed class ProvisioningFixture
    {
        private ProvisioningFixture(string baseDirectory, bool valid)
        {
            Layout = new LocalAsrRuntimeLayout(baseDirectory);
            Inspector = new ControlledInspector(Layout, valid);
            Provisioning = new LocalAsrProvisioning(
                Layout,
                Inspector,
                _ =>
                {
                    PipelineFactoryCalls++;
                    return new SuccessfulLocalPipeline();
                });
        }

        internal LocalAsrRuntimeLayout Layout { get; }
        internal ControlledInspector Inspector { get; }
        internal LocalAsrProvisioning Provisioning { get; }
        internal int PipelineFactoryCalls { get; private set; }

        internal static ProvisioningFixture Valid(string baseDirectory) => new(baseDirectory, true);
        internal static ProvisioningFixture Missing(string baseDirectory) => new(baseDirectory, false);
    }

    private sealed class ControlledInspector : ILocalAsrFileInspector
    {
        private readonly LocalAsrRuntimeLayout layout;
        private readonly Dictionary<string, LocalAsrFileInspection> values =
            new(StringComparer.OrdinalIgnoreCase);

        internal ControlledInspector(LocalAsrRuntimeLayout layout, bool valid)
        {
            this.layout = layout;
            if (valid)
                SetAllValid();
        }

        internal int CallCount { get; private set; }

        public LocalAsrFileInspection Inspect(string path, bool calculateSha256)
        {
            CallCount++;
            return values.TryGetValue(path, out var value) ? value : Missing();
        }

        internal LocalAsrFileInspection Get(LocalAsrAssetIdentity identity) =>
            values[PathFor(identity)];

        internal void Set(LocalAsrAssetIdentity identity, LocalAsrFileInspection inspection) =>
            values[PathFor(identity)] = inspection;

        internal void SetAllValid()
        {
            Set(LocalAsrAssetIdentity.WorkerExecutable, new(true, false, 1, null));
            Set(LocalAsrAssetIdentity.OnnxRuntime, new(
                true,
                false,
                LocalAsrProvisioning.OnnxRuntimeLength,
                null));
            Set(LocalAsrAssetIdentity.SileroVadModel, new(
                true,
                false,
                LocalAsrProvisioning.SileroVadModelLength,
                LocalAsrProvisioning.SileroVadModelSha256));
            Set(LocalAsrAssetIdentity.WhisperModel, new(
                true,
                false,
                LocalAsrProvisioning.WhisperModelLength,
                LocalAsrProvisioning.WhisperModelSha256));
        }

        private string PathFor(LocalAsrAssetIdentity identity) => identity switch
        {
            LocalAsrAssetIdentity.WorkerExecutable => layout.WorkerExecutablePath,
            LocalAsrAssetIdentity.OnnxRuntime => layout.OnnxRuntimePath,
            LocalAsrAssetIdentity.SileroVadModel => layout.SileroVadModelPath,
            LocalAsrAssetIdentity.WhisperModel => layout.WhisperModelPath,
            _ => throw new ArgumentOutOfRangeException(nameof(identity), identity, null)
        };
    }

    private sealed class SuccessfulLocalPipeline : ILocalAsrPipeline
    {
        public Guid? SessionId { get; private set; }
        public event EventHandler<CaptionEvent>? CaptionEventReceived;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionId = Guid.NewGuid();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void Emit(CaptionEvent captionEvent) => CaptionEventReceived?.Invoke(this, captionEvent);
    }

    private sealed class SuccessfulCaptionSource(string sourceId) : ICaptionSource
    {
        private Guid? sessionId;

        public string SourceId { get; } = sourceId;
        public CaptionSourceState State { get; private set; } = CaptionSourceState.Stopped;
        public string? FailureReason => null;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public event EventHandler<CaptionEvent>? CaptionEventReceived;
        public event EventHandler<CaptionSourceStatus>? StatusChanged;

        public Task<CaptionSourceStartResult> StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            State = CaptionSourceState.Running;
            sessionId = Guid.NewGuid();
            return Task.FromResult(CaptionSourceStartResult.Started(sessionId.Value));
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            State = CaptionSourceState.Stopped;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        internal void Emit(CaptionEvent captionEvent) => CaptionEventReceived?.Invoke(this, captionEvent);
        internal void Emit(CaptionSourceStatus status) => StatusChanged?.Invoke(this, status);
    }

    private sealed class AsyncDisposableScope(IAsyncDisposable value) : IDisposable
    {
        public void Dispose() => value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
