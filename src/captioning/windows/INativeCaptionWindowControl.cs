namespace LiveCaptionsTranslator.captioning.windows
{
    public enum CaptionWindowControlFailure
    {
        Unavailable,
        Faulted,
        Cancelled
    }

    public sealed record CaptionWindowControlResult(
        bool Success,
        CaptionWindowControlFailure? Failure,
        string? FailureReason)
    {
        public static CaptionWindowControlResult Completed() => new(true, null, null);

        public static CaptionWindowControlResult Failed(
            CaptionWindowControlFailure failure,
            string reason) => new(false, failure, reason);
    }

    public interface INativeCaptionWindowControl
    {
        bool IsWindowAvailable { get; }
        bool? IsWindowVisible { get; }
        Task<CaptionWindowControlResult> ShowAsync(CancellationToken cancellationToken = default);
        Task<CaptionWindowControlResult> HideAsync(CancellationToken cancellationToken = default);
    }
}
