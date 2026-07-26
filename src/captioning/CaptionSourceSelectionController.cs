using LiveCaptionsTranslator.captioning.local;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.captioning
{
    public enum LocalAsrProvisioningState
    {
        Unknown,
        Ready,
        Unavailable
    }

    public sealed record CaptionSourceApplicationStatus(
        CaptionSourceKind PersistedSource,
        CaptionSourceKind? ActiveSource,
        CaptionSourceState SourceState,
        bool IsSelectionInProgress,
        LocalAsrProvisioningState LocalAsrProvisioning,
        string? SourceFailureReason,
        string? PreferencePersistenceFailureReason,
        string? LocalAsrProvisioningFailureReason);

    public sealed record CaptionSourceSelectionResult(
        bool Success,
        bool SourceSelectionSucceeded,
        bool PreferencePersisted,
        CaptionSourceKind RequestedSource,
        CaptionSourceApplicationStatus Status,
        string? FailureReason);

    internal sealed class CaptionSourceSelectionController
    {
        private readonly Setting setting;
        private readonly Func<CaptionSourceKind, CancellationToken, Task<CaptionSourceStartResult>>
            selectSourceAsync;
        private readonly Func<LocalAsrProvisioningResult> validateLocalAsr;
        private readonly Func<CaptionSourceKind?> readActiveSource;
        private readonly Func<CaptionSourceState> readSourceState;
        private readonly Func<string?> readSourceFailure;
        private readonly CaptionSourceUiFailureSanitizer failureSanitizer;
        private readonly SemaphoreSlim operationGate = new(1, 1);
        private readonly object stateLock = new();

        private bool selectionInProgress;
        private LocalAsrProvisioningState provisioningState = LocalAsrProvisioningState.Unknown;
        private string? provisioningFailure;
        private string? selectionFailure;
        private string? persistenceFailure;

        private const string PreferencePersistenceFailure =
            "The caption source changed, but the preference could not be saved.";

        internal CaptionSourceSelectionController(
            Setting setting,
            Func<CaptionSourceKind, CancellationToken, Task<CaptionSourceStartResult>>
                selectSourceAsync,
            Func<LocalAsrProvisioningResult> validateLocalAsr,
            Func<CaptionSourceKind?> readActiveSource,
            Func<CaptionSourceState> readSourceState,
            Func<string?> readSourceFailure,
            CaptionSourceUiFailureSanitizer failureSanitizer)
        {
            this.setting = setting ?? throw new ArgumentNullException(nameof(setting));
            this.selectSourceAsync = selectSourceAsync ??
                throw new ArgumentNullException(nameof(selectSourceAsync));
            this.validateLocalAsr = validateLocalAsr ??
                throw new ArgumentNullException(nameof(validateLocalAsr));
            this.readActiveSource = readActiveSource ??
                throw new ArgumentNullException(nameof(readActiveSource));
            this.readSourceState = readSourceState ??
                throw new ArgumentNullException(nameof(readSourceState));
            this.readSourceFailure = readSourceFailure ??
                throw new ArgumentNullException(nameof(readSourceFailure));
            this.failureSanitizer = failureSanitizer ??
                throw new ArgumentNullException(nameof(failureSanitizer));
        }

        internal event EventHandler<CaptionSourceApplicationStatus>? StatusChanged;

        internal CaptionSourceApplicationStatus Status => CaptureStatus();

        internal async Task<CaptionSourceStartResult> StartConfiguredAsync(
            CancellationToken cancellationToken = default)
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            SetSelectionInProgress(true);
            try
            {
                var requested = setting.CaptionSource;
                var result = await selectSourceAsync(requested, cancellationToken)
                    .ConfigureAwait(false);
                SetSelectionFailure(result.Success
                    ? null
                    : failureSanitizer.Sanitize(result.FailureReason));
                return result;
            }
            finally
            {
                SetSelectionInProgress(false);
                operationGate.Release();
            }
        }

        internal async Task<CaptionSourceSelectionResult> SelectAsync(
            CaptionSourceKind requested,
            CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(requested))
                throw new ArgumentOutOfRangeException(nameof(requested), requested, "Caption source is not supported.");

            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            SetSelectionInProgress(true);
            var success = false;
            var sourceSelectionSucceeded = false;
            var preferencePersisted = false;
            var provisioningRejected = false;
            string? failure = null;
            try
            {
                if (requested == CaptionSourceKind.LocalAsr)
                {
                    var provisioning = await ValidateLocalAsrCoreAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!provisioning.IsProvisioned)
                    {
                        SetSelectionFailure(null);
                        provisioningRejected = true;
                        failure = failureSanitizer.Sanitize(provisioning.FailureReason) ??
                            "Local ASR is unavailable.";
                    }
                }

                if (!provisioningRejected)
                {
                    var startResult = await selectSourceAsync(requested, cancellationToken)
                        .ConfigureAwait(false);
                    if (!startResult.Success)
                    {
                        failure = failureSanitizer.Sanitize(startResult.FailureReason);
                        SetSelectionFailure(failure);
                    }
                    else
                    {
                        sourceSelectionSucceeded = true;
                        SetSelectionFailure(null);
                        var persistence = setting.PersistCaptionSource(requested);
                        if (!persistence.Success)
                        {
                            failure = PreferencePersistenceFailure;
                            SetPersistenceFailure(failure);
                            System.Diagnostics.Debug.WriteLine(
                                $"Caption-source preference persistence failed: {persistence.Failure}");
                        }
                        else
                        {
                            SetPersistenceFailure(null);
                            preferencePersisted = true;
                            success = true;
                        }
                    }
                }

            }
            finally
            {
                SetSelectionInProgress(false);
                operationGate.Release();
            }

            return new CaptionSourceSelectionResult(
                success,
                sourceSelectionSucceeded,
                preferencePersisted,
                requested,
                CaptureStatus(),
                failure);
        }

        internal async Task<LocalAsrProvisioningResult> RefreshLocalAsrProvisioningAsync(
            CancellationToken cancellationToken = default)
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ValidateLocalAsrCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                operationGate.Release();
            }
        }

        internal void NotifySourceStatusChanged()
        {
            if (readSourceState() == CaptionSourceState.Running)
                SetSelectionFailure(null);
            else
                PublishStatus();
        }

        private async Task<LocalAsrProvisioningResult> ValidateLocalAsrCoreAsync(
            CancellationToken cancellationToken)
        {
            var result = await Task.Run(validateLocalAsr, cancellationToken).ConfigureAwait(false);
            lock (stateLock)
            {
                provisioningState = result.IsProvisioned
                    ? LocalAsrProvisioningState.Ready
                    : LocalAsrProvisioningState.Unavailable;
                provisioningFailure = result.IsProvisioned
                    ? null
                    : failureSanitizer.Sanitize(result.FailureReason);
            }
            PublishStatus();
            return result;
        }

        private void SetSelectionInProgress(bool value)
        {
            lock (stateLock)
                selectionInProgress = value;
            PublishStatus();
        }

        private void SetSelectionFailure(string? value)
        {
            lock (stateLock)
                selectionFailure = value;
            PublishStatus();
        }

        private void SetPersistenceFailure(string? value)
        {
            lock (stateLock)
                persistenceFailure = value;
            PublishStatus();
        }

        private CaptionSourceApplicationStatus CaptureStatus()
        {
            bool busy;
            LocalAsrProvisioningState localProvisioning;
            string? localFailure;
            string? currentSelectionFailure;
            string? currentPersistenceFailure;
            lock (stateLock)
            {
                busy = selectionInProgress;
                localProvisioning = provisioningState;
                localFailure = provisioningFailure;
                currentSelectionFailure = selectionFailure;
                currentPersistenceFailure = persistenceFailure;
            }

            return new CaptionSourceApplicationStatus(
                setting.CaptionSource,
                readActiveSource(),
                readSourceState(),
                busy,
                localProvisioning,
                currentSelectionFailure ?? failureSanitizer.Sanitize(readSourceFailure()),
                currentPersistenceFailure,
                localFailure);
        }

        private void PublishStatus()
        {
            var status = CaptureStatus();
            var handlers = StatusChanged;
            if (handlers == null)
                return;

            foreach (EventHandler<CaptionSourceApplicationStatus> handler in handlers.GetInvocationList())
            {
                try { handler(this, status); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"A caption-source settings status subscriber failed: {ex}");
                }
            }
        }
    }

    internal sealed class CaptionSourceUiFailureSanitizer
    {
        private const string HiddenPath = "[path]";

        private static readonly System.Text.RegularExpressions.Regex UncRoot = new(
            @"(?<![\\/])(?:\\\\|//)[^\\/\s'\""<>|]+[\\/][^\\/\s'\""<>|]+",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static readonly System.Text.RegularExpressions.Regex DriveRoot = new(
            @"(?<![A-Za-z0-9])[A-Za-z]:[\\/]",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private readonly (string ExpectedPath, string RelativePath)[] knownPaths;

        internal CaptionSourceUiFailureSanitizer(LocalAsrRuntimeLayout layout)
        {
            ArgumentNullException.ThrowIfNull(layout);
            knownPaths =
            [
                (layout.WorkerExecutablePath,
                    $@"asr\{LocalAsrRuntimeLayout.WorkerExecutableFileName}"),
                (layout.OnnxRuntimePath,
                    $@"asr\{LocalAsrRuntimeLayout.OnnxRuntimeFileName}"),
                (layout.SileroVadModelPath,
                    $@"asr\{LocalAsrRuntimeLayout.SileroVadModelFileName}"),
                (layout.WhisperModelPath,
                    $@"asr\{LocalAsrRuntimeLayout.WhisperModelFileName}")
            ];
        }

        internal string? Sanitize(string? failure)
        {
            if (failure == null)
                return null;

            var sanitized = failure;
            foreach (var knownPath in knownPaths)
            {
                sanitized = sanitized.Replace(
                    knownPath.ExpectedPath,
                    knownPath.RelativePath,
                    StringComparison.OrdinalIgnoreCase);
            }

            sanitized = UncRoot.Replace(sanitized, HiddenPath);
            sanitized = DriveRoot.Replace(sanitized, $@"{HiddenPath}\");
            return sanitized;
        }
    }

    public sealed class CaptionSourceSettingsSession : IDisposable
    {
        private readonly CaptionSourceSelectionController controller;
        private int disposed;

        internal CaptionSourceSettingsSession(CaptionSourceSelectionController controller)
        {
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            controller.StatusChanged += OnControllerStatusChanged;
        }

        public CaptionSourceApplicationStatus Status => controller.Status;

        public event EventHandler<CaptionSourceApplicationStatus>? StatusChanged;

        public Task<CaptionSourceSelectionResult> SelectAsync(
            CaptionSourceKind requested,
            CancellationToken cancellationToken = default) =>
            controller.SelectAsync(requested, cancellationToken);

        public Task<LocalAsrProvisioningResult> RefreshLocalAsrProvisioningAsync(
            CancellationToken cancellationToken = default) =>
            controller.RefreshLocalAsrProvisioningAsync(cancellationToken);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            controller.StatusChanged -= OnControllerStatusChanged;
            StatusChanged = null;
        }

        private void OnControllerStatusChanged(
            object? sender,
            CaptionSourceApplicationStatus status)
        {
            if (Volatile.Read(ref disposed) == 0)
                StatusChanged?.Invoke(this, status);
        }
    }
}
