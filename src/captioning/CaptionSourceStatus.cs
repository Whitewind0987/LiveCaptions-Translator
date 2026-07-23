namespace LiveCaptionsTranslator.captioning
{
    public sealed record CaptionSourceStatus
    {
        public string SourceId { get; }
        public CaptionSourceState State { get; }
        public string? FailureReason { get; }
        public DateTimeOffset ChangedAtUtc { get; }

        public CaptionSourceStatus(
            string sourceId,
            CaptionSourceState state,
            string? failureReason,
            DateTimeOffset changedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("Caption source identity must not be empty.", nameof(sourceId));
            if (!Enum.IsDefined(state))
                throw new ArgumentOutOfRangeException(nameof(state), state, "Caption source state is not supported.");
            if (failureReason != null && string.IsNullOrWhiteSpace(failureReason))
                throw new ArgumentException("Failure reason must be null or non-whitespace text.", nameof(failureReason));
            if (changedAtUtc == default || changedAtUtc.Offset != TimeSpan.Zero)
                throw new ArgumentException("Status timestamp must be a non-default UTC timestamp.",
                    nameof(changedAtUtc));

            SourceId = sourceId;
            State = state;
            FailureReason = failureReason;
            ChangedAtUtc = changedAtUtc;
        }
    }
}
