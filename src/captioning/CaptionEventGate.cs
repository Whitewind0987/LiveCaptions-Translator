namespace LiveCaptionsTranslator.captioning
{
    public sealed class CaptionEventGate
    {
        private readonly HashSet<Guid> seenSessions = [];
        private string? currentText;
        private CaptionEventKind? currentKind;

        public Guid? ActiveSessionId { get; private set; }
        public long LastAcceptedSequence { get; private set; }
        public long HighestObservedSequence { get; private set; }
        public long? CurrentSegmentId { get; private set; }
        public long? CurrentRevision { get; private set; }
        public bool IsCurrentSegmentFinal { get; private set; }

        public bool TryAccept(CaptionEvent captionEvent, out string? rejectionReason)
        {
            ArgumentNullException.ThrowIfNull(captionEvent);

            if (captionEvent.Kind == CaptionEventKind.Reset)
                return TryAcceptReset(captionEvent, out rejectionReason);

            if (!ActiveSessionId.HasValue)
                return Reject("A Reset must establish an active session before text events.", out rejectionReason);
            if (captionEvent.SessionId != ActiveSessionId.Value)
            {
                return Reject(
                    seenSessions.Contains(captionEvent.SessionId)
                        ? "The caption event belongs to an obsolete session."
                        : "The caption event belongs to a session that has not been established by Reset.",
                    out rejectionReason);
            }
            if (!TryObserveNewerSequence(captionEvent.Sequence, out rejectionReason))
                return false;

            if (!CurrentSegmentId.HasValue)
            {
                if (captionEvent.SegmentId != 1)
                    return Reject("The first text segment after Reset must use segment identity 1.",
                        out rejectionReason);
                if (captionEvent.Revision != 1)
                    return Reject("The first revision of a segment must be revision 1.", out rejectionReason);

                AcceptTextEvent(captionEvent);
                rejectionReason = null;
                return true;
            }

            if (captionEvent.SegmentId < CurrentSegmentId.Value)
                return Reject("Caption segment identities must not decrease.", out rejectionReason);

            if (captionEvent.SegmentId > CurrentSegmentId.Value)
            {
                if (captionEvent.SegmentId != CurrentSegmentId.Value + 1)
                    return Reject("A new caption segment must immediately follow the current segment.",
                        out rejectionReason);
                if (!IsCurrentSegmentFinal)
                    return Reject("A new caption segment cannot begin before the current segment is final.",
                        out rejectionReason);
                if (captionEvent.Revision != 1)
                    return Reject("The first revision of a segment must be revision 1.", out rejectionReason);

                AcceptTextEvent(captionEvent);
                rejectionReason = null;
                return true;
            }

            if (IsCurrentSegmentFinal)
                return Reject("A finalized caption segment cannot be updated.", out rejectionReason);
            if (captionEvent.Revision < CurrentRevision)
                return Reject("Caption segment revisions must not decrease.", out rejectionReason);

            if (captionEvent.Revision > CurrentRevision)
            {
                if (string.Equals(captionEvent.Text, currentText, StringComparison.Ordinal))
                    return Reject("A newer caption revision must contain changed text.", out rejectionReason);

                AcceptTextEvent(captionEvent);
                rejectionReason = null;
                return true;
            }

            if (!string.Equals(captionEvent.Text, currentText, StringComparison.Ordinal))
                return Reject("Events for the same segment revision must contain identical text.",
                    out rejectionReason);
            if (LifecycleRank(captionEvent.Kind) <= LifecycleRank(currentKind!.Value))
                return Reject("Caption lifecycle may only progress forward within a segment revision.",
                    out rejectionReason);

            AcceptTextEvent(captionEvent);
            rejectionReason = null;
            return true;
        }

        private bool TryAcceptReset(CaptionEvent captionEvent, out string? rejectionReason)
        {
            if (!ActiveSessionId.HasValue || captionEvent.SessionId != ActiveSessionId.Value)
            {
                if (captionEvent.Sequence != 1)
                    return Reject("The first Reset for a new session must use sequence 1.", out rejectionReason);
                if (seenSessions.Contains(captionEvent.SessionId))
                    return Reject("The Reset belongs to an obsolete session.", out rejectionReason);

                seenSessions.Add(captionEvent.SessionId);
                ActiveSessionId = captionEvent.SessionId;
                LastAcceptedSequence = captionEvent.Sequence;
                HighestObservedSequence = captionEvent.Sequence;
                ClearSegmentState();
                rejectionReason = null;
                return true;
            }

            if (!TryObserveNewerSequence(captionEvent.Sequence, out rejectionReason))
                return false;

            LastAcceptedSequence = captionEvent.Sequence;
            ClearSegmentState();
            rejectionReason = null;
            return true;
        }

        private bool TryObserveNewerSequence(long sequence, out string? rejectionReason)
        {
            if (sequence == HighestObservedSequence)
                return Reject("Caption event sequence is duplicated.", out rejectionReason);
            if (sequence < HighestObservedSequence)
                return Reject("Caption event sequence is older than the highest observed sequence.",
                    out rejectionReason);

            HighestObservedSequence = sequence;
            rejectionReason = null;
            return true;
        }

        private void AcceptTextEvent(CaptionEvent captionEvent)
        {
            LastAcceptedSequence = captionEvent.Sequence;
            CurrentSegmentId = captionEvent.SegmentId;
            CurrentRevision = captionEvent.Revision;
            currentText = captionEvent.Text;
            currentKind = captionEvent.Kind;
            IsCurrentSegmentFinal = captionEvent.Kind == CaptionEventKind.Final;
        }

        private void ClearSegmentState()
        {
            CurrentSegmentId = null;
            CurrentRevision = null;
            currentText = null;
            currentKind = null;
            IsCurrentSegmentFinal = false;
        }

        private static int LifecycleRank(CaptionEventKind kind) => kind switch
        {
            CaptionEventKind.Partial => 0,
            CaptionEventKind.Committed => 1,
            CaptionEventKind.Final => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "Reset does not participate in text-segment lifecycle progression.")
        };

        private static bool Reject(string reason, out string? rejectionReason)
        {
            rejectionReason = reason;
            return false;
        }
    }
}
