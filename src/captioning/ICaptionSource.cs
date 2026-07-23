namespace LiveCaptionsTranslator.captioning
{
    /// <summary>
    /// Provides source-independent caption events and lifecycle state.
    /// </summary>
    /// <remarks>
    /// Start implementations must be idempotent or reject duplicate starts predictably.
    /// Stop must be safe when the source is already stopped, and no caption events may be
    /// emitted after stop completes. Every successful start or restart creates a new caption
    /// session whose first event is a Reset with sequence 1.
    /// </remarks>
    public interface ICaptionSource : IAsyncDisposable
    {
        string SourceId { get; }
        CaptionSourceState State { get; }
        string? FailureReason { get; }

        event EventHandler<CaptionEvent>? CaptionEventReceived;
        event EventHandler<CaptionSourceStatus>? StatusChanged;

        Task<CaptionSourceStartResult> StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
    }
}
