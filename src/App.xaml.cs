using System.Diagnostics;
using System.Windows;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        private readonly CancellationTokenSource applicationCancellation = new();
        private Task[] backgroundLoops = [];
        private int startupInvoked;
        private int shutdownInvoked;

        public App()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            if (Interlocked.Exchange(ref startupInvoked, 1) != 0)
                return;

            try
            {
                Translator.Setting?.Save();
                await Translator.StartCaptionSourceAsync(applicationCancellation.Token);
            }
            catch (OperationCanceledException) when (applicationCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Application caption-source startup failed: {ex}");
                Translator.ApplyCaptionSourceUnavailableWarning();
            }
            finally
            {
                if (!applicationCancellation.IsCancellationRequested)
                    StartBackgroundLoops();
                base.OnStartup(e);
            }
        }

        private void StartBackgroundLoops()
        {
            backgroundLoops =
            [
                Translator.SyncLoop(applicationCancellation.Token),
                Translator.TranslateLoop(applicationCancellation.Token),
                Translator.DisplayLoop(applicationCancellation.Token)
            ];

            foreach (var loop in backgroundLoops)
            {
                _ = loop.ContinueWith(
                    completed => Debug.WriteLine(
                        $"A Translator background loop failed: {completed.Exception}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Application shutdown failed: {ex}");
            }
            finally
            {
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                applicationCancellation.Dispose();
                base.OnExit(e);
            }
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            try
            {
                ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Final caption-source cleanup failed: {ex}");
            }
        }

        private async Task ShutdownAsync()
        {
            if (Interlocked.Exchange(ref shutdownInvoked, 1) != 0)
                return;

            applicationCancellation.Cancel();

            if (backgroundLoops.Length > 0)
            {
                try
                {
                    await Task.WhenAll(backgroundLoops).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await Translator.StopCaptionSourceAsync(CancellationToken.None).ConfigureAwait(false);
            await Translator.DisposeCaptionSourceAsync().ConfigureAwait(false);
        }
    }
}
