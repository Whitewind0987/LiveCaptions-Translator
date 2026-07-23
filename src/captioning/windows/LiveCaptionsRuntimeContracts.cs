namespace LiveCaptionsTranslator.captioning.windows
{
    internal enum LiveCaptionsRuntimeFailureCategory
    {
        Unavailable,
        Faulted
    }

    internal sealed record LiveCaptionsRuntimeInitializationResult(
        bool Success,
        LiveCaptionsRuntimeFailureCategory? FailureCategory,
        string? FailureReason)
    {
        internal static LiveCaptionsRuntimeInitializationResult Started() => new(true, null, null);

        internal static LiveCaptionsRuntimeInitializationResult Failed(
            LiveCaptionsRuntimeFailureCategory category,
            string reason) => new(false, category, reason);
    }

    internal enum LiveCaptionsSnapshotReadStatus
    {
        Success,
        NoText,
        WindowLost,
        Faulted,
        Cancelled
    }

    internal sealed record LiveCaptionsSnapshotReadResult(
        LiveCaptionsSnapshotReadStatus Status,
        string? Text,
        string? FailureReason)
    {
        internal static LiveCaptionsSnapshotReadResult Snapshot(string text) =>
            new(LiveCaptionsSnapshotReadStatus.Success, text, null);

        internal static LiveCaptionsSnapshotReadResult NoText() =>
            new(LiveCaptionsSnapshotReadStatus.NoText, null, null);

        internal static LiveCaptionsSnapshotReadResult WindowLost(string reason) =>
            new(LiveCaptionsSnapshotReadStatus.WindowLost, null, reason);

        internal static LiveCaptionsSnapshotReadResult Faulted(string reason) =>
            new(LiveCaptionsSnapshotReadStatus.Faulted, null, reason);

        internal static LiveCaptionsSnapshotReadResult Cancelled() =>
            new(LiveCaptionsSnapshotReadStatus.Cancelled, null, null);
    }

    internal sealed record LiveCaptionsRuntimeCleanupResult(bool Success, string? FailureReason)
    {
        internal static LiveCaptionsRuntimeCleanupResult Cleaned() => new(true, null);
        internal static LiveCaptionsRuntimeCleanupResult Failed(string reason) => new(false, reason);
    }

    internal interface ILiveCaptionsRuntime : IAsyncDisposable
    {
        bool IsWindowAvailable { get; }
        bool? IsWindowVisible { get; }

        Task<LiveCaptionsRuntimeInitializationResult> InitializeAsync(
            CancellationToken cancellationToken = default);

        Task<LiveCaptionsSnapshotReadResult> ReadSnapshotAsync(
            CancellationToken cancellationToken = default);

        Task<CaptionWindowControlResult> ShowAsync(CancellationToken cancellationToken = default);
        Task<CaptionWindowControlResult> HideAsync(CancellationToken cancellationToken = default);

        Task<LiveCaptionsRuntimeCleanupResult> ShutdownAsync(
            CancellationToken cancellationToken = default);
    }

    internal interface ICaptionSourceDelay
    {
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    internal sealed class SystemCaptionSourceDelay : ICaptionSourceDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }
}
