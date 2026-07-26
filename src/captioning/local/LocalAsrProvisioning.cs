using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;

using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.windows;
using LiveCaptionsTranslator.worker;

namespace LiveCaptionsTranslator.captioning.local
{
    public enum LocalAsrAssetIdentity
    {
        WorkerExecutable,
        OnnxRuntime,
        SileroVadModel,
        WhisperModel
    }

    public sealed record LocalAsrAssetStatus(
        LocalAsrAssetIdentity Identity,
        string ApplicationRelativePath,
        string ExpectedPath,
        bool Exists,
        bool IsFile,
        long? ActualLength,
        long? ExpectedLength,
        string? ExpectedSha256,
        bool IsValid,
        string? FailureReason);

    public sealed record LocalAsrProvisioningResult
    {
        internal LocalAsrProvisioningResult(IEnumerable<LocalAsrAssetStatus> assets)
        {
            var values = assets?.ToArray() ?? throw new ArgumentNullException(nameof(assets));
            Assets = new ReadOnlyCollection<LocalAsrAssetStatus>(values);
            WorkerExecutable = Find(values, LocalAsrAssetIdentity.WorkerExecutable);
            OnnxRuntime = Find(values, LocalAsrAssetIdentity.OnnxRuntime);
            SileroVadModel = Find(values, LocalAsrAssetIdentity.SileroVadModel);
            WhisperModel = Find(values, LocalAsrAssetIdentity.WhisperModel);
            IsProvisioned = values.All(asset => asset.IsValid);
            FailureReason = IsProvisioned
                ? null
                : string.Join(" ", values
                    .Where(asset => !asset.IsValid)
                    .Select(asset => asset.FailureReason));
        }

        public bool IsProvisioned { get; }
        public IReadOnlyList<LocalAsrAssetStatus> Assets { get; }
        public LocalAsrAssetStatus WorkerExecutable { get; }
        public LocalAsrAssetStatus OnnxRuntime { get; }
        public LocalAsrAssetStatus SileroVadModel { get; }
        public LocalAsrAssetStatus WhisperModel { get; }
        public string? FailureReason { get; }

        private static LocalAsrAssetStatus Find(
            IEnumerable<LocalAsrAssetStatus> assets,
            LocalAsrAssetIdentity identity) =>
            assets.Single(asset => asset.Identity == identity);
    }

    internal sealed class LocalAsrRuntimeLayout
    {
        internal const string RuntimeDirectoryName = "asr";
        internal const string WorkerExecutableFileName = "LiveCaptionsAsrWorker.exe";
        internal const string OnnxRuntimeFileName = "onnxruntime.dll";
        internal const string SileroVadModelFileName = "silero_vad_16k_op15.onnx";
        internal const string WhisperModelFileName = "ggml-tiny.bin";

        private static readonly HashSet<string> FixedFileNames = new(StringComparer.Ordinal)
        {
            WorkerExecutableFileName,
            OnnxRuntimeFileName,
            SileroVadModelFileName,
            WhisperModelFileName
        };

        internal LocalAsrRuntimeLayout(string applicationBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(applicationBaseDirectory))
                throw new ArgumentException("An application base directory is required.", nameof(applicationBaseDirectory));
            if (!Path.IsPathFullyQualified(applicationBaseDirectory))
                throw new ArgumentException("The application base directory must be absolute.", nameof(applicationBaseDirectory));

            try
            {
                ApplicationBaseDirectory = Path.GetFullPath(applicationBaseDirectory);
                RuntimeRoot = Path.GetFullPath(Path.Combine(
                    ApplicationBaseDirectory,
                    RuntimeDirectoryName));
                WorkerExecutablePath = ResolveFixedFileName(WorkerExecutableFileName);
                OnnxRuntimePath = ResolveFixedFileName(OnnxRuntimeFileName);
                SileroVadModelPath = ResolveFixedFileName(SileroVadModelFileName);
                WhisperModelPath = ResolveFixedFileName(WhisperModelFileName);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException(
                    "The application base directory could not be canonicalized.",
                    nameof(applicationBaseDirectory),
                    ex);
            }
        }

        internal string ApplicationBaseDirectory { get; }
        internal string RuntimeRoot { get; }
        internal string WorkerExecutablePath { get; }
        internal string OnnxRuntimePath { get; }
        internal string SileroVadModelPath { get; }
        internal string WhisperModelPath { get; }

        internal string ResolveFixedFileName(string fileName)
        {
            if (!FixedFileNames.Contains(fileName))
                throw new ArgumentException("The filename is not part of the fixed Local ASR runtime layout.", nameof(fileName));

            var resolved = Path.GetFullPath(Path.Combine(RuntimeRoot, fileName));
            if (!Contains(resolved))
                throw new ArgumentException("The resolved asset path is outside the Local ASR runtime root.", nameof(fileName));
            return resolved;
        }

        internal bool Contains(string path)
        {
            var canonical = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(RuntimeRoot, canonical);
            return !Path.IsPathRooted(relative) &&
                !string.Equals(relative, "..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        }
    }

    internal sealed record LocalAsrFileInspection(
        bool Exists,
        bool IsDirectory,
        long? Length,
        string? Sha256);

    internal interface ILocalAsrFileInspector
    {
        LocalAsrFileInspection Inspect(string path, bool calculateSha256);
    }

    internal sealed class ProductionLocalAsrFileInspector : ILocalAsrFileInspector
    {
        public LocalAsrFileInspection Inspect(string path, bool calculateSha256)
        {
            if (Directory.Exists(path))
                return new LocalAsrFileInspection(true, true, null, null);
            if (!File.Exists(path))
                return new LocalAsrFileInspection(false, false, null, null);

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            var hash = calculateSha256
                ? Convert.ToHexString(SHA256.HashData(stream))
                : null;
            return new LocalAsrFileInspection(true, false, stream.Length, hash);
        }
    }

    internal sealed class LocalAsrProvisioning
    {
        internal const long OnnxRuntimeLength = 14_107_168;
        internal const long SileroVadModelLength = 1_289_603;
        internal const long WhisperModelLength = 77_691_713;
        internal const string SileroVadModelSha256 =
            "7ED98DDBAD84CCAC4CD0AEB3099049280713DF825C610A8ED34543318F1B2C49";
        internal const string WhisperModelSha256 =
            "BE07E048E1E599AD46341C8D2A135645097A538221678B7ACDD1B1919C6E1B21";

        private readonly LocalAsrRuntimeLayout layout;
        private readonly ILocalAsrFileInspector fileInspector;
        private readonly Func<LocalAsrRuntimeLayout, ILocalAsrPipeline> pipelineFactory;

        internal LocalAsrProvisioning(string applicationBaseDirectory)
            : this(
                new LocalAsrRuntimeLayout(applicationBaseDirectory),
                new ProductionLocalAsrFileInspector(),
                CreateProductionPipeline)
        {
        }

        internal LocalAsrProvisioning(
            LocalAsrRuntimeLayout layout,
            ILocalAsrFileInspector fileInspector,
            Func<LocalAsrRuntimeLayout, ILocalAsrPipeline> pipelineFactory)
        {
            this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
            this.fileInspector = fileInspector ?? throw new ArgumentNullException(nameof(fileInspector));
            this.pipelineFactory = pipelineFactory ?? throw new ArgumentNullException(nameof(pipelineFactory));
        }

        internal LocalAsrRuntimeLayout Layout => layout;

        internal LocalAsrProvisioningResult Validate()
        {
            var definitions = new[]
            {
                new AssetDefinition(
                    LocalAsrAssetIdentity.WorkerExecutable,
                    LocalAsrRuntimeLayout.WorkerExecutableFileName,
                    ExpectedLength: null,
                    ExpectedSha256: null),
                new AssetDefinition(
                    LocalAsrAssetIdentity.OnnxRuntime,
                    LocalAsrRuntimeLayout.OnnxRuntimeFileName,
                    OnnxRuntimeLength,
                    ExpectedSha256: null),
                new AssetDefinition(
                    LocalAsrAssetIdentity.SileroVadModel,
                    LocalAsrRuntimeLayout.SileroVadModelFileName,
                    SileroVadModelLength,
                    SileroVadModelSha256),
                new AssetDefinition(
                    LocalAsrAssetIdentity.WhisperModel,
                    LocalAsrRuntimeLayout.WhisperModelFileName,
                    WhisperModelLength,
                    WhisperModelSha256)
            };
            return new LocalAsrProvisioningResult(definitions.Select(ValidateAsset));
        }

        internal ICaptionSource CreateSource()
        {
            var result = Validate();
            if (!result.IsProvisioned)
            {
                throw new InvalidOperationException(
                    $"Local ASR is not provisioned: {result.FailureReason}");
            }

            return new LocalAsrCaptionSource(() => pipelineFactory(layout));
        }

        private LocalAsrAssetStatus ValidateAsset(AssetDefinition definition)
        {
            var relativePath = $@"asr\{definition.FileName}";
            var expectedPath = layout.ResolveFixedFileName(definition.FileName);
            if (!layout.Contains(expectedPath))
            {
                return Invalid(
                    definition,
                    relativePath,
                    expectedPath,
                    exists: false,
                    isFile: false,
                    actualLength: null,
                    $"{relativePath} resolves outside the Local ASR runtime root.");
            }

            LocalAsrFileInspection inspection;
            try
            {
                inspection = fileInspector.Inspect(
                    expectedPath,
                    calculateSha256: definition.ExpectedSha256 != null);
            }
            catch
            {
                return Invalid(
                    definition,
                    relativePath,
                    expectedPath,
                    exists: false,
                    isFile: false,
                    actualLength: null,
                    $"{relativePath} could not be inspected.");
            }

            if (!inspection.Exists)
            {
                return Invalid(
                    definition,
                    relativePath,
                    expectedPath,
                    exists: false,
                    isFile: false,
                    actualLength: null,
                    $"{relativePath} is missing.");
            }
            if (inspection.IsDirectory)
            {
                return Invalid(
                    definition,
                    relativePath,
                    expectedPath,
                    exists: true,
                    isFile: false,
                    actualLength: null,
                    $"{relativePath} must be a file.");
            }
            if (inspection.Length is null or <= 0)
            {
                return Invalid(
                    definition,
                    relativePath,
                    expectedPath,
                    exists: true,
                    isFile: true,
                    inspection.Length,
                    $"{relativePath} must be non-empty.");
            }
            if (definition.ExpectedLength is long expectedLength &&
                inspection.Length != expectedLength)
            {
                return Invalid(
                    definition,
                    relativePath,
                    expectedPath,
                    exists: true,
                    isFile: true,
                    inspection.Length,
                    $"{relativePath} has an invalid length; expected {expectedLength} bytes.");
            }
            if (definition.ExpectedSha256 != null &&
                !string.Equals(
                    inspection.Sha256,
                    definition.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(
                    definition,
                    relativePath,
                    expectedPath,
                    exists: true,
                    isFile: true,
                    inspection.Length,
                    $"{relativePath} has an invalid SHA-256 digest.");
            }

            return new LocalAsrAssetStatus(
                definition.Identity,
                relativePath,
                expectedPath,
                Exists: true,
                IsFile: true,
                inspection.Length,
                definition.ExpectedLength,
                definition.ExpectedSha256,
                IsValid: true,
                FailureReason: null);
        }

        private static LocalAsrAssetStatus Invalid(
            AssetDefinition definition,
            string relativePath,
            string expectedPath,
            bool exists,
            bool isFile,
            long? actualLength,
            string failureReason) =>
            new(
                definition.Identity,
                relativePath,
                expectedPath,
                exists,
                isFile,
                actualLength,
                definition.ExpectedLength,
                definition.ExpectedSha256,
                IsValid: false,
                failureReason);

        private static ILocalAsrPipeline CreateProductionPipeline(
            LocalAsrRuntimeLayout layout) =>
            CreateProductionPipeline(
                layout,
                () => new AudioCaptureService(
                    new WindowsAudioEndpointProvider(),
                    new WasapiLoopbackCaptureRuntimeFactory()),
                (workerPath, recognition) => new AsrWorkerSupervisor(
                    workerPath,
                    recognition: recognition),
                (capture, supervisor) => new AudioWorkerPipeline(capture, supervisor),
                (vadPath, whisperPath, language, threadCount) =>
                    WorkerRecognitionConfiguration.Create(
                        vadPath,
                        whisperPath,
                        language,
                        threadCount));

        internal static ILocalAsrPipeline CreateProductionPipeline(
            LocalAsrRuntimeLayout layout,
            Func<AudioCaptureService> captureFactory,
            Func<string, WorkerRecognitionConfiguration, AsrWorkerSupervisor> supervisorFactory,
            Func<AudioCaptureService, AsrWorkerSupervisor, AudioWorkerPipeline> audioPipelineFactory,
            Func<string, string, string, int, WorkerRecognitionConfiguration> recognitionFactory)
        {
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(captureFactory);
            ArgumentNullException.ThrowIfNull(supervisorFactory);
            ArgumentNullException.ThrowIfNull(audioPipelineFactory);
            ArgumentNullException.ThrowIfNull(recognitionFactory);

            var recognition = recognitionFactory(
                layout.SileroVadModelPath,
                layout.WhisperModelPath,
                "auto",
                WorkerRecognitionConfiguration.DefaultThreadCount) ??
                throw new InvalidOperationException("The Local ASR recognition factory returned null.");
            var supervisor = supervisorFactory(layout.WorkerExecutablePath, recognition) ??
                throw new InvalidOperationException("The Local ASR supervisor factory returned null.");
            var capture = captureFactory() ??
                throw new InvalidOperationException("The Local ASR capture factory returned null.");
            var pipeline = audioPipelineFactory(capture, supervisor) ??
                throw new InvalidOperationException("The Local ASR audio pipeline factory returned null.");
            return new AudioWorkerPipelineCaptionAdapter(pipeline, endpointId: null);
        }

        private sealed record AssetDefinition(
            LocalAsrAssetIdentity Identity,
            string FileName,
            long? ExpectedLength,
            string? ExpectedSha256);
    }
}
