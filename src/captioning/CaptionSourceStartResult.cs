namespace LiveCaptionsTranslator.captioning
{
    public sealed record CaptionSourceStartResult
    {
        public bool Success { get; }
        public CaptionSourceState State { get; }
        public Guid? SessionId { get; }
        public string? FailureReason { get; }

        private CaptionSourceStartResult(
            bool success,
            CaptionSourceState state,
            Guid? sessionId,
            string? failureReason)
        {
            Success = success;
            State = state;
            SessionId = sessionId;
            FailureReason = failureReason;
        }

        public static CaptionSourceStartResult Started(Guid sessionId)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentException("A successful start must provide a non-empty session identity.",
                    nameof(sessionId));

            return new CaptionSourceStartResult(true, CaptionSourceState.Running, sessionId, null);
        }

        public static CaptionSourceStartResult Failed(CaptionSourceState state, string failureReason)
        {
            if (state != CaptionSourceState.Unavailable && state != CaptionSourceState.Faulted)
            {
                throw new ArgumentOutOfRangeException(nameof(state), state,
                    "A failed start must report Unavailable or Faulted state.");
            }
            if (string.IsNullOrWhiteSpace(failureReason))
                throw new ArgumentException("A failed start must provide a failure reason.", nameof(failureReason));

            return new CaptionSourceStartResult(false, state, null, failureReason);
        }
    }
}
