using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.captioning;
using LiveCaptionsTranslator.captioning.windows;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private const string UnavailableSourceWarning = "[WARNING] No caption source is available.";
        private const string RestartingSourceWarning =
            "[WARNING] LiveCaptions was unexpectedly closed, restarting...";

        private static readonly ConcurrentQueue<string> pendingTextQueue = new();
        private static readonly TranslationTaskQueue translationTaskQueue = new();
        private static readonly CaptionSourceHost captionSourceHost;
        private static readonly Caption? caption;
        private static readonly Setting? setting;

        public static Caption? Caption => caption;
        public static Setting? Setting => setting;

        public static bool LogOnlyFlag { get; set; }
        public static bool FirstUseFlag { get; set; }
        public static CaptionSourceState CaptionSourceState => captionSourceHost.State;
        public static bool CaptionSourceUnavailable =>
            CaptionSourceState is CaptionSourceState.Unavailable or CaptionSourceState.Faulted;
        public static string? CaptionSourceFailureReason => captionSourceHost.FailureReason;
        public static bool IsCaptionWindowAvailable =>
            captionSourceHost.NativeWindowControl?.IsWindowAvailable == true;
        public static bool? IsCaptionWindowVisible =>
            captionSourceHost.NativeWindowControl?.IsWindowVisible;

        public static event Action? TranslationLogged;

        static Translator()
        {
            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = models.Caption.GetInstance();
            setting = models.Setting.Load();

            var source = new WindowsLiveCaptionsSource();
            captionSourceHost = new CaptionSourceHost(source);
            captionSourceHost.StatusChanged += OnCaptionSourceStatusChanged;
        }

        public static Task<CaptionSourceStartResult> StartCaptionSourceAsync(
            CancellationToken cancellationToken = default) =>
            captionSourceHost.StartAsync(cancellationToken);

        public static Task StopCaptionSourceAsync(CancellationToken cancellationToken = default) =>
            captionSourceHost.StopAsync(cancellationToken);

        public static ValueTask DisposeCaptionSourceAsync() => captionSourceHost.DisposeAsync();

        public static Task<CaptionWindowControlResult> ShowCaptionWindowAsync(
            CancellationToken cancellationToken = default) =>
            captionSourceHost.NativeWindowControl?.ShowAsync(cancellationToken) ??
            Task.FromResult(CaptionWindowControlResult.Failed(
                CaptionWindowControlFailure.Unavailable,
                "The active caption source does not provide a native window."));

        public static Task<CaptionWindowControlResult> HideCaptionWindowAsync(
            CancellationToken cancellationToken = default) =>
            captionSourceHost.NativeWindowControl?.HideAsync(cancellationToken) ??
            Task.FromResult(CaptionWindowControlResult.Failed(
                CaptionWindowControlFailure.Unavailable,
                "The active caption source does not provide a native window."));

        public static void ApplyCaptionSourceUnavailableWarning()
        {
            Caption.DisplayOriginalCaption = UnavailableSourceWarning;
            Caption.DisplayTranslatedCaption = UnavailableSourceWarning;
            Caption.OverlayOriginalCaption = UnavailableSourceWarning;
            Caption.OverlayNoticePrefix = string.Empty;
            Caption.OverlayCurrentTranslation = UnavailableSourceWarning;
        }

        public static async Task SyncLoop(CancellationToken cancellationToken = default)
        {
            var processor = new CaptionSnapshotProcessor();

            while (!cancellationToken.IsCancellationRequested)
            {
                var sourceState = captionSourceHost.ReadLatestState();
                var result = processor.Tick(
                    sourceState.SessionGeneration,
                    sourceState.Snapshot?.Text,
                    Caption.Contexts.Count,
                    Setting.DisplaySentences,
                    Setting.MaxSyncInterval,
                    Setting.MaxIdleInterval);

                if (result.SessionReset)
                {
                    Caption.OriginalCaption = string.Empty;
                    Caption.OverlayOriginalCaption = " ";
                    if (!CaptionSourceUnavailable)
                        Caption.DisplayOriginalCaption = string.Empty;
                }

                if (result.HasSnapshot)
                {
                    if (result.ClearContexts)
                        ClearContexts();

                    Caption.OverlayOriginalCaption = result.OverlayOriginalCaption!;
                    if (!string.Equals(
                            Caption.DisplayOriginalCaption,
                            result.DisplayOriginalCaption,
                            StringComparison.Ordinal))
                    {
                        Caption.DisplayOriginalCaption = result.DisplayOriginalCaption!;
                    }
                    if (!string.Equals(
                            Caption.OriginalCaption,
                            result.OriginalCaption,
                            StringComparison.Ordinal))
                    {
                        Caption.OriginalCaption = result.OriginalCaption!;
                    }

                    if (!string.IsNullOrEmpty(result.TranslationTextToEnqueue))
                        pendingTextQueue.Enqueue(result.TranslationTextToEnqueue);
                }

                try
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public static async Task TranslateLoop(CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (pendingTextQueue.TryDequeue(out var originalSnapshot))
                {
                    if (LogOnlyFlag)
                    {
                        bool isOverwrite = await IsOverwrite(originalSnapshot, cancellationToken);
                        await LogOnly(originalSnapshot, isOverwrite, cancellationToken);
                    }
                    else
                    {
                        translationTaskQueue.Enqueue(token => Task.Run(
                            () => Translate(originalSnapshot, token), token), originalSnapshot);
                    }
                }

                try
                {
                    await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public static async Task DisplayLoop(CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var (translatedText, isChoke) = translationTaskQueue.Output;

                if (LogOnlyFlag)
                {
                    Caption.TranslatedCaption = string.Empty;
                    Caption.DisplayTranslatedCaption = "[Paused]";
                    Caption.OverlayNoticePrefix = "[Paused]";
                    Caption.OverlayCurrentTranslation = string.Empty;
                }
                else if (!string.IsNullOrEmpty(RegexPatterns.NoticePrefix().Replace(
                             translatedText, string.Empty).Trim()) &&
                         string.CompareOrdinal(Caption.TranslatedCaption, translatedText) != 0)
                {
                    Caption.TranslatedCaption = translatedText;
                    Caption.DisplayTranslatedCaption =
                        TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);

                    if (Caption.TranslatedCaption.Contains("[ERROR]") || Caption.TranslatedCaption.Contains("[WARNING]"))
                        Caption.OverlayCurrentTranslation = Caption.TranslatedCaption;
                    else
                    {
                        var match = RegexPatterns.NoticePrefixAndTranslation().Match(Caption.TranslatedCaption);
                        Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                        Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
                    }
                }

                try
                {
                    if (isChoke)
                        await Task.Delay(720, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private static void OnCaptionSourceStatusChanged(object? sender, CaptionSourceStatus status)
        {
            switch (status.State)
            {
                case CaptionSourceState.Running:
                    ClearObsoleteSourceWarnings();
                    break;
                case CaptionSourceState.Restarting:
                    Caption.DisplayTranslatedCaption = RestartingSourceWarning;
                    Caption.OverlayNoticePrefix = string.Empty;
                    Caption.OverlayCurrentTranslation = RestartingSourceWarning;
                    break;
                case CaptionSourceState.Unavailable:
                case CaptionSourceState.Faulted:
                    ApplyCaptionSourceUnavailableWarning();
                    break;
            }
        }

        private static void ClearObsoleteSourceWarnings()
        {
            if (Caption.DisplayOriginalCaption == UnavailableSourceWarning)
                Caption.DisplayOriginalCaption = string.Empty;
            if (Caption.DisplayTranslatedCaption is UnavailableSourceWarning or RestartingSourceWarning)
                Caption.DisplayTranslatedCaption = string.Empty;
            if (Caption.OverlayOriginalCaption == UnavailableSourceWarning)
                Caption.OverlayOriginalCaption = " ";
            if (Caption.OverlayCurrentTranslation is UnavailableSourceWarning or RestartingSourceWarning)
                Caption.OverlayCurrentTranslation = " ";
        }

        public static async Task<(string, bool)> Translate(string text, CancellationToken token = default)
        {
            string translatedText;
            bool isChoke = Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;

            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                if (Setting.ContextAware && !TranslateAPI.IsLLMBased)
                {
                    translatedText = await TranslateAPI.TranslateFunction($"{Caption.AwareContextsCaption} 🔤 {text} 🔤", token);
                    translatedText = RegexPatterns.TargetSentence().Match(translatedText).Groups[1].Value;
                }
                else
                {
                    translatedText = await TranslateAPI.TranslateFunction(text, token);
                    translatedText = translatedText.Replace("🔤", "");
                }

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds,4} ms] " + translatedText;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ($"[ERROR] Translation Failed: {ex.Message}", isChoke);
            }

            return (translatedText, isChoke);
        }

        public static async Task Log(string originalText, string translatedText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task AddContexts(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;

            if (Caption.Contexts.Count >= models.Caption.MAX_CONTEXTS)
                Caption.Contexts.Dequeue();
            Caption.Contexts.Enqueue(lastLog);

            Caption.OnPropertyChanged("DisplayLogCards");
            Caption.OnPropertyChanged("OverlayPreviousTranslation");
        }

        public static void ClearContexts()
        {
            Caption.Contexts.Clear();

            Caption.OnPropertyChanged("DisplayLogCards");
            Caption.OnPropertyChanged("OverlayPreviousTranslation");
        }

        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;

            int minLen = Math.Min(originalText.Length, lastOriginalText.Length);
            originalText = originalText.Substring(0, minLen);
            lastOriginalText = lastOriginalText.Substring(0, minLen);

            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > TextUtil.SIM_THRESHOLD;
        }
    }
}
