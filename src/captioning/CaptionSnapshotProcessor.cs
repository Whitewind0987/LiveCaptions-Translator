using System.Text;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.captioning
{
    public sealed record CaptionProcessingTickResult(
        bool SessionReset,
        bool HasSnapshot,
        bool ClearContexts,
        string? DisplayOriginalCaption,
        string? OverlayOriginalCaption,
        string? OriginalCaption,
        string? TranslationTextToEnqueue,
        int IdleCount,
        int SyncCount);

    public sealed class CaptionSnapshotProcessor
    {
        private long observedSessionGeneration;
        private string previousOriginalCaption = string.Empty;
        private int idleCount;
        private int syncCount;

        public CaptionProcessingTickResult Tick(
            long sessionGeneration,
            string? rawSnapshot,
            int contextCount,
            int displaySentenceCount,
            int maxSyncInterval,
            int maxIdleInterval)
        {
            var sessionReset = sessionGeneration != observedSessionGeneration;
            if (sessionReset)
            {
                observedSessionGeneration = sessionGeneration;
                previousOriginalCaption = string.Empty;
                idleCount = 0;
                syncCount = 0;
            }

            if (string.IsNullOrWhiteSpace(rawSnapshot))
            {
                return new CaptionProcessingTickResult(
                    sessionReset, false, false, null, null, null, null, idleCount, syncCount);
            }

            var fullText = Preprocess(rawSnapshot);
            var clearContexts = fullText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && contextCount > 0;
            var effectiveContextCount = clearContexts ? 0 : contextCount;

            int lastEosIndex;
            if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                lastEosIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
            else
                lastEosIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);

            var latestCaption = fullText[(lastEosIndex + 1)..];
            if (lastEosIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
            {
                lastEosIndex = fullText[0..lastEosIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                latestCaption = fullText[(lastEosIndex + 1)..];
            }

            var overlayOriginalCaption = latestCaption;
            for (var historyCount = Math.Min(displaySentenceCount, effectiveContextCount);
                 historyCount > 0 && lastEosIndex > 0;
                 historyCount--)
            {
                lastEosIndex = fullText[0..lastEosIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                overlayOriginalCaption = fullText[(lastEosIndex + 1)..];
            }

            var displayOriginalCaption =
                TextUtil.ShortenDisplaySentence(latestCaption, TextUtil.VERYLONG_THRESHOLD);

            var originalCaption = latestCaption;
            var lastEos = originalCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
            if (lastEos != -1)
                originalCaption = originalCaption[..(lastEos + 1)];

            string? translationText = null;
            if (!string.Equals(previousOriginalCaption, originalCaption, StringComparison.Ordinal))
            {
                previousOriginalCaption = originalCaption;
                idleCount = 0;
                if (originalCaption.Length > 0 &&
                    Array.IndexOf(TextUtil.PUNC_EOS, originalCaption[^1]) != -1)
                {
                    syncCount = 0;
                    translationText = originalCaption;
                }
                else if (Encoding.UTF8.GetByteCount(originalCaption) >= TextUtil.SHORT_THRESHOLD)
                {
                    syncCount++;
                }
            }
            else
            {
                idleCount++;
            }

            if (originalCaption.Length > 0 &&
                (syncCount > maxSyncInterval || idleCount == maxIdleInterval))
            {
                syncCount = 0;
                translationText = originalCaption;
            }

            return new CaptionProcessingTickResult(
                sessionReset,
                true,
                clearContexts,
                displayOriginalCaption,
                overlayOriginalCaption,
                originalCaption,
                translationText,
                idleCount,
                syncCount);
        }

        public static string Preprocess(string fullText)
        {
            fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
            fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
            fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
            fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
            return TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);
        }
    }
}
