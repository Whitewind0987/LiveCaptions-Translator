namespace LiveCaptionsTranslator.captioning
{
    public sealed record CaptionEvent
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; }
        public Guid SessionId { get; }
        public long Sequence { get; }
        public long SegmentId { get; }
        public long Revision { get; }
        public CaptionEventKind Kind { get; }
        public string Text { get; }
        public long? AudioStartMilliseconds { get; }
        public long? AudioEndMilliseconds { get; }
        public DateTimeOffset EmittedAtUtc { get; }

        public CaptionEvent(
            int schemaVersion,
            Guid sessionId,
            long sequence,
            long segmentId,
            long revision,
            CaptionEventKind kind,
            string text,
            long? audioStartMilliseconds,
            long? audioEndMilliseconds,
            DateTimeOffset emittedAtUtc)
        {
            if (schemaVersion != CurrentSchemaVersion)
                throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion,
                    $"Only caption-event schema version {CurrentSchemaVersion} is supported.");
            if (sessionId == Guid.Empty)
                throw new ArgumentException("Caption session identity must not be empty.", nameof(sessionId));
            if (sequence < 1)
                throw new ArgumentOutOfRangeException(nameof(sequence), sequence,
                    "Caption event sequence must be at least 1.");
            if (!Enum.IsDefined(kind))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Caption event kind is not supported.");
            ArgumentNullException.ThrowIfNull(text);
            if (audioStartMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(audioStartMilliseconds), audioStartMilliseconds,
                    "Audio start timestamp must be non-negative.");
            if (audioEndMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(audioEndMilliseconds), audioEndMilliseconds,
                    "Audio end timestamp must be non-negative.");
            if (audioStartMilliseconds.HasValue && audioEndMilliseconds.HasValue &&
                audioEndMilliseconds.Value < audioStartMilliseconds.Value)
            {
                throw new ArgumentException(
                    "Audio end timestamp must not be earlier than the audio start timestamp.",
                    nameof(audioEndMilliseconds));
            }
            if (emittedAtUtc == default || emittedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException(
                    "Caption event emission timestamp must be a non-default UTC timestamp.",
                    nameof(emittedAtUtc));
            }

            if (kind == CaptionEventKind.Reset)
            {
                if (text.Length != 0)
                    throw new ArgumentException("Reset caption events must contain empty text.", nameof(text));
                if (segmentId != 0)
                    throw new ArgumentOutOfRangeException(nameof(segmentId), segmentId,
                        "Reset caption events must use segment identity 0.");
                if (revision != 0)
                    throw new ArgumentOutOfRangeException(nameof(revision), revision,
                        "Reset caption events must use revision 0.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(text))
                    throw new ArgumentException("Text caption events must contain recognized text.", nameof(text));
                if (segmentId < 1)
                    throw new ArgumentOutOfRangeException(nameof(segmentId), segmentId,
                        "Text caption events must use a segment identity of at least 1.");
                if (revision < 1)
                    throw new ArgumentOutOfRangeException(nameof(revision), revision,
                        "Text caption events must use a revision of at least 1.");
            }

            SchemaVersion = schemaVersion;
            SessionId = sessionId;
            Sequence = sequence;
            SegmentId = segmentId;
            Revision = revision;
            Kind = kind;
            Text = text;
            AudioStartMilliseconds = audioStartMilliseconds;
            AudioEndMilliseconds = audioEndMilliseconds;
            EmittedAtUtc = emittedAtUtc;
        }
    }
}
