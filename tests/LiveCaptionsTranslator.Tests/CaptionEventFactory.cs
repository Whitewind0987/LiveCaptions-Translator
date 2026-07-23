using LiveCaptionsTranslator.captioning;

namespace LiveCaptionsTranslator.Tests;

internal static class CaptionEventFactory
{
    internal static readonly Guid SessionA = new("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid SessionB = new("22222222-2222-2222-2222-222222222222");
    internal static readonly DateTimeOffset EmittedAtUtc =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static CaptionEvent Reset(Guid? sessionId = null, long sequence = 1) =>
        new(
            CaptionEvent.CurrentSchemaVersion,
            sessionId ?? SessionA,
            sequence,
            0,
            0,
            CaptionEventKind.Reset,
            string.Empty,
            null,
            null,
            EmittedAtUtc);

    internal static CaptionEvent Text(
        CaptionEventKind kind = CaptionEventKind.Partial,
        Guid? sessionId = null,
        long sequence = 2,
        long segmentId = 1,
        long revision = 1,
        string text = "caption") =>
        new(
            CaptionEvent.CurrentSchemaVersion,
            sessionId ?? SessionA,
            sequence,
            segmentId,
            revision,
            kind,
            text,
            100,
            200,
            EmittedAtUtc);
}
