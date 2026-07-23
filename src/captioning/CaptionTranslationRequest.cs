namespace LiveCaptionsTranslator.captioning
{
    public sealed record CaptionTranslationRequest
    {
        public Guid SessionId { get; }
        public long Sequence { get; }
        public long SegmentId { get; }
        public long Revision { get; }
        public string Text { get; }
        public CaptionEventKind OriginatingEventKind { get; }
        public bool IsCommitted => OriginatingEventKind == CaptionEventKind.Committed;
        public bool IsFinal => OriginatingEventKind == CaptionEventKind.Final;

        private CaptionTranslationRequest(CaptionEvent captionEvent)
        {
            SessionId = captionEvent.SessionId;
            Sequence = captionEvent.Sequence;
            SegmentId = captionEvent.SegmentId;
            Revision = captionEvent.Revision;
            Text = captionEvent.Text;
            OriginatingEventKind = captionEvent.Kind;
        }

        public static CaptionTranslationRequest FromCaptionEvent(CaptionEvent captionEvent)
        {
            ArgumentNullException.ThrowIfNull(captionEvent);
            if (captionEvent.Kind != CaptionEventKind.Committed && captionEvent.Kind != CaptionEventKind.Final)
            {
                throw new ArgumentException(
                    "Translation requests may only be created from Committed or Final caption events.",
                    nameof(captionEvent));
            }

            return new CaptionTranslationRequest(captionEvent);
        }
    }
}
