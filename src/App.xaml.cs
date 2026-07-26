using System.Diagnostics;
using System.Windows;

using LiveCaptionsTranslator.lifecycle;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        private readonly CancellationTokenSource applicationCancellation = new();
        private Task[] backgroundLoops = [];
        private Stage65AcceptanceDriver? acceptanceDriver;
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

            base.OnStartup(e);
            Stage65AcceptanceTrace.Write("app.main-window.creating");
            MainWindow = new MainWindow();
            Stage65AcceptanceTrace.Write("app.main-window.created");
            MainWindow.Show();
            Stage65AcceptanceTrace.Write("app.main-window.shown", new
            {
                MainWindow.IsVisible,
                MainWindow.IsLoaded,
                MainWindow.Title,
                AppContext.BaseDirectory,
                CurrentDirectory = Environment.CurrentDirectory,
                NativeHandle = new System.Windows.Interop.WindowInteropHelper(MainWindow)
                    .Handle.ToInt64()
            });
            StartAcceptanceTrace();
            try
            {
                Stage65AcceptanceTrace.Write("app.settings-save.starting");
                Translator.Setting?.Save();
                Stage65AcceptanceTrace.Write("app.settings-save.completed");
                Stage65AcceptanceTrace.Write("app.caption-source.starting");
                var startResult = await Translator.StartCaptionSourceAsync(
                    applicationCancellation.Token);
                Stage65AcceptanceTrace.Write("app.caption-source.completed", new
                {
                    Result = startResult,
                    Status = Translator.CaptionSourceApplicationStatus
                });
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
                {
                    StartBackgroundLoops();
                    Stage65AcceptanceTrace.Write("app.background-loops.started", new
                    {
                        Count = backgroundLoops.Length,
                        Tasks = backgroundLoops.Select(loop => new
                        {
                            loop.Id,
                            loop.Status
                        })
                    });
                    acceptanceDriver = Stage65AcceptanceDriver.TryStart(
                        MainWindow, applicationCancellation.Token);
                }
            }
        }

        private static void StartAcceptanceTrace()
        {
            if (!Stage65AcceptanceTrace.IsEnabled || Translator.Caption is not { } caption)
                return;

            var tracker = new Stage65AcceptanceValueTracker<CaptionDisplaySnapshot>();
            CaptionDisplaySnapshot ReadSnapshot() => new(
                caption.DisplayOriginalCaption,
                caption.DisplayTranslatedCaption,
                caption.OverlayOriginalCaption,
                caption.OverlayCurrentTranslation);

            var initial = ReadSnapshot();
            tracker.TryObserve(initial);
            Stage65AcceptanceTrace.Write("caption.display.snapshot", initial);
            caption.PropertyChanged += (_, args) =>
            {
                var snapshot = ReadSnapshot();
                if (!tracker.TryObserve(snapshot))
                    return;

                Stage65AcceptanceTrace.Write("caption.display.changed", new
                {
                    args.PropertyName,
                    snapshot.DisplayOriginalCaption,
                    snapshot.DisplayTranslatedCaption,
                    snapshot.OverlayOriginalCaption,
                    snapshot.OverlayCurrentTranslation
                });
            };
            Translator.TranslationLogged += () =>
                Stage65AcceptanceTrace.Write("history.translation-logged");
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
                    completed =>
                    {
                        Debug.WriteLine(
                            $"A Translator background loop failed: {completed.Exception}");
                        Stage65AcceptanceTrace.Write("app.background-loop.failed", new
                        {
                            completed.Id,
                            Exception = completed.Exception?.ToString()
                        });
                    },
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

            var failures = await ApplicationShutdownCoordinator.RunAsync(
                applicationCancellation.Cancel,
                backgroundLoops,
                () => Translator.StopCaptionSourceAsync(CancellationToken.None),
                Translator.DisposeCaptionSourceAsync).ConfigureAwait(false);

            if (acceptanceDriver != null)
            {
                await acceptanceDriver.DisposeAsync().ConfigureAwait(false);
                acceptanceDriver = null;
            }

            foreach (var failure in failures)
            {
                Debug.WriteLine(
                    $"Application shutdown phase '{failure.Phase}' failed: {failure.Exception}");
            }
        }

        private sealed record CaptionDisplaySnapshot(
            string DisplayOriginalCaption,
            string DisplayTranslatedCaption,
            string OverlayOriginalCaption,
            string OverlayCurrentTranslation);
    }
}
