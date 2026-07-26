using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

using LiveCaptionsTranslator.captioning;

namespace LiveCaptionsTranslator.utils
{
    internal sealed record Stage65AcceptanceAction(
        string Name,
        string? Value,
        int TimeoutSeconds);

    internal sealed record Stage65AcceptancePlan(
        string SessionId,
        IReadOnlyList<Stage65AcceptanceAction> Actions);

    internal sealed record Stage65AcceptanceConfiguration(
        string PlanPath,
        string EvidencePath,
        Stage65AcceptancePlan Plan);

    internal sealed class Stage65AcceptanceSessionLifetime : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private long generation = 1;
        private int disposeStarted;

        internal Stage65AcceptanceSessionLifetime(
            CancellationToken applicationCancellation = default) =>
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                applicationCancellation);

        internal CancellationToken Token => cancellation.Token;
        internal long Capture() => Volatile.Read(ref generation);

        internal bool IsCurrent(long capturedGeneration) =>
            !cancellation.IsCancellationRequested &&
            capturedGeneration == Volatile.Read(ref generation);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
                return;

            Interlocked.Increment(ref generation);
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            cancellation.Dispose();
        }
    }

    internal sealed class Stage65AcceptanceDriver : IAsyncDisposable
    {
        internal const string ActionPlanPathEnvironmentVariable =
            "LCT_STAGE65_ACTION_PLAN_PATH";

        private static readonly HashSet<string> AllowedActions =
        [
            "open-overlay",
            "enable-log-only",
            "disable-log-only",
            "open-settings",
            "refresh-local-asr",
            "select-windows",
            "select-local",
            "wait-display-contains",
            "wait-history-logged",
            "wait-source-running",
            "wait-source-failed",
            "close-main-window"
        ];

        private readonly MainWindow mainWindow;
        private readonly Stage65AcceptanceConfiguration configuration;
        private readonly Stage65AcceptanceSessionLifetime lifetime;
        private readonly Task runTask;
        private int disposeStarted;

        private Stage65AcceptanceDriver(
            MainWindow mainWindow,
            Stage65AcceptanceConfiguration configuration,
            CancellationToken applicationCancellation)
        {
            this.mainWindow = mainWindow;
            this.configuration = configuration;
            lifetime = new Stage65AcceptanceSessionLifetime(applicationCancellation);
            runTask = Task.Run(() => RunAsync(lifetime.Token));
        }

        internal static Stage65AcceptanceDriver? TryStart(
            Window? mainWindow,
            CancellationToken applicationCancellation)
        {
            if (mainWindow is not MainWindow typedWindow)
                return null;

            if (!TryLoadConfiguration(
                    Environment.GetEnvironmentVariable(
                        ActionPlanPathEnvironmentVariable),
                    Environment.GetEnvironmentVariable(
                        Stage65AcceptanceTrace.EvidencePathEnvironmentVariable),
                    out var configuration,
                    out var failure))
            {
                if (failure != null)
                    System.Diagnostics.Debug.WriteLine(
                        $"Stage 6.5 acceptance driver is disabled: {failure}");
                return null;
            }

            Stage65AcceptanceTrace.Write("acceptance.driver.started", new
            {
                configuration.Plan.SessionId,
                configuration.PlanPath,
                ActionCount = configuration.Plan.Actions.Count
            });
            return new Stage65AcceptanceDriver(
                typedWindow, configuration, applicationCancellation);
        }

        internal static bool TryLoadConfiguration(
            string? planPath,
            string? evidencePath,
            out Stage65AcceptanceConfiguration configuration,
            out string? failure)
        {
            configuration = null!;
            failure = null;
            if (string.IsNullOrWhiteSpace(planPath) &&
                string.IsNullOrWhiteSpace(evidencePath))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(planPath) ||
                !Path.IsPathFullyQualified(planPath))
            {
                failure = "The action-plan path must be absolute.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(evidencePath) ||
                !Path.IsPathFullyQualified(evidencePath))
            {
                failure = "The evidence path must be absolute.";
                return false;
            }

            try
            {
                if (!File.Exists(planPath))
                {
                    failure = "The action-plan file does not exist.";
                    return false;
                }

                if (!TryParsePlan(File.ReadAllText(planPath), out var plan, out failure))
                    return false;

                configuration = new Stage65AcceptanceConfiguration(
                    Path.GetFullPath(planPath),
                    Path.GetFullPath(evidencePath),
                    plan);
                return true;
            }
            catch (Exception ex)
            {
                failure = $"The action plan could not be read: {ex.Message}";
                return false;
            }
        }

        internal static bool TryParsePlan(
            string json,
            out Stage65AcceptancePlan plan,
            out string? failure)
        {
            plan = null!;
            failure = null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("sessionId", out var sessionElement) ||
                    string.IsNullOrWhiteSpace(sessionElement.GetString()) ||
                    !root.TryGetProperty("actions", out var actionsElement) ||
                    actionsElement.ValueKind != JsonValueKind.Array)
                {
                    failure = "The action plan must contain sessionId and actions.";
                    return false;
                }

                var actions = new List<Stage65AcceptanceAction>();
                foreach (var element in actionsElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object ||
                        !element.TryGetProperty("name", out var nameElement))
                    {
                        failure = "Every action must contain a name.";
                        return false;
                    }

                    var name = nameElement.GetString();
                    if (string.IsNullOrWhiteSpace(name) || !AllowedActions.Contains(name))
                    {
                        failure = $"Unsupported acceptance action: {name ?? "<null>"}.";
                        return false;
                    }

                    string? value = null;
                    if (element.TryGetProperty("value", out var valueElement) &&
                        valueElement.ValueKind != JsonValueKind.Null)
                    {
                        value = valueElement.GetString();
                    }
                    if (name == "wait-display-contains" &&
                        string.IsNullOrWhiteSpace(value))
                    {
                        failure = "wait-display-contains requires a non-empty value.";
                        return false;
                    }

                    var timeoutSeconds = 60;
                    if (element.TryGetProperty("timeoutSeconds", out var timeoutElement) &&
                        (!timeoutElement.TryGetInt32(out timeoutSeconds) ||
                         timeoutSeconds is < 1 or > 300))
                    {
                        failure = "Action timeoutSeconds must be between 1 and 300.";
                        return false;
                    }

                    actions.Add(new Stage65AcceptanceAction(
                        name, value, timeoutSeconds));
                }

                if (actions.Count == 0)
                {
                    failure = "The action plan must contain at least one action.";
                    return false;
                }

                plan = new Stage65AcceptancePlan(
                    sessionElement.GetString()!, actions);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                failure = $"The action plan is invalid: {ex.Message}";
                return false;
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await WaitUntilAsync(
                    () => OnDispatcherAsync(
                        () => mainWindow.IsLoaded && mainWindow.IsVisible,
                        cancellationToken),
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);

                for (var index = 0; index < configuration.Plan.Actions.Count; index++)
                {
                    var action = configuration.Plan.Actions[index];
                    var actionGeneration = lifetime.Capture();
                    Stage65AcceptanceTrace.Write("acceptance.action.started", new
                    {
                        configuration.Plan.SessionId,
                        Index = index,
                        action.Name,
                        action.Value,
                        action.TimeoutSeconds
                    });

                    await ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!lifetime.IsCurrent(actionGeneration))
                        throw new OperationCanceledException(cancellationToken);
                    Stage65AcceptanceTrace.Write("acceptance.action.completed", new
                    {
                        configuration.Plan.SessionId,
                        Index = index,
                        action.Name,
                        Status = Translator.CaptionSourceApplicationStatus,
                        Translator.LogOnlyFlag,
                        DisplayOriginalCaption = Translator.Caption?.DisplayOriginalCaption,
                        OverlayOriginalCaption = Translator.Caption?.OverlayOriginalCaption
                    });
                }

                Stage65AcceptanceTrace.Write("acceptance.driver.completed", new
                {
                    configuration.Plan.SessionId
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Stage65AcceptanceTrace.Write("acceptance.driver.canceled", new
                {
                    configuration.Plan.SessionId
                });
            }
            catch (Exception ex)
            {
                Stage65AcceptanceTrace.Write("acceptance.driver.failed", new
                {
                    configuration.Plan.SessionId,
                    Exception = ex.ToString()
                });
            }
        }

        private Task ExecuteAsync(
            Stage65AcceptanceAction action,
            CancellationToken cancellationToken) => action.Name switch
        {
            "open-overlay" => InvokeMainButtonAsync(
                () => mainWindow.OverlayModeButton,
                () => mainWindow.OverlayWindow is { IsLoaded: true, IsVisible: true },
                action,
                cancellationToken),
            "enable-log-only" => InvokeMainButtonAsync(
                () => mainWindow.LogOnlyButton,
                () => Translator.LogOnlyFlag,
                action,
                cancellationToken),
            "disable-log-only" => InvokeMainButtonAsync(
                () => mainWindow.LogOnlyButton,
                () => !Translator.LogOnlyFlag,
                action,
                cancellationToken),
            "open-settings" => OpenSettingsAsync(action, cancellationToken),
            "refresh-local-asr" => RefreshProvisioningAsync(action, cancellationToken),
            "select-windows" => SelectSourceAsync(0, action, cancellationToken),
            "select-local" => SelectSourceAsync(1, action, cancellationToken),
            "wait-display-contains" => WaitForDisplayChangeAsync(
                action.Value!, action, cancellationToken),
            "wait-history-logged" => WaitForHistoryAsync(action, cancellationToken),
            "wait-source-running" => WaitForStatusAsync(
                status => status.ActiveSource == CaptionSourceKind.LocalAsr &&
                          status.SourceState == CaptionSourceState.Running,
                action,
                cancellationToken),
            "wait-source-failed" => WaitForStatusAsync(
                status => status.SourceState is CaptionSourceState.Faulted or
                    CaptionSourceState.Unavailable,
                action,
                cancellationToken),
            "close-main-window" => CloseMainWindowAsync(cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported acceptance action '{action.Name}'.")
        };

        private async Task InvokeMainButtonAsync(
            Func<ButtonBase> button,
            Func<bool> completed,
            Stage65AcceptanceAction action,
            CancellationToken cancellationToken)
        {
            if (!await OnDispatcherAsync(completed, cancellationToken).ConfigureAwait(false))
            {
                await OnDispatcherAsync(() => InvokeButton(button()), cancellationToken)
                    .ConfigureAwait(false);
            }

            await WaitUntilAsync(
                () => OnDispatcherAsync(completed, cancellationToken),
                TimeSpan.FromSeconds(action.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task OpenSettingsAsync(
            Stage65AcceptanceAction action,
            CancellationToken cancellationToken)
        {
            await OnDispatcherAsync(
                () => mainWindow.RootNavigation.Navigate(typeof(SettingPage)),
                cancellationToken).ConfigureAwait(false);
            await WaitUntilAsync(
                () => OnDispatcherAsync(
                    () => FindVisualChild<SettingPage>(mainWindow) is { IsLoaded: true },
                    cancellationToken),
                TimeSpan.FromSeconds(action.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task RefreshProvisioningAsync(
            Stage65AcceptanceAction action,
            CancellationToken cancellationToken)
        {
            var page = await RequireSettingPageAsync(cancellationToken).ConfigureAwait(false);
            await OnDispatcherAsync(
                () => InvokeButton(page.RefreshLocalAsrProvisioningButton),
                cancellationToken).ConfigureAwait(false);
            await WaitUntilAsync(
                () => OnDispatcherAsync(
                    () => page.RefreshLocalAsrProvisioningButton.IsEnabled &&
                          page.LocalAsrProvisioningStatus.Text != "Not checked",
                    cancellationToken),
                TimeSpan.FromSeconds(action.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task SelectSourceAsync(
            int selectedIndex,
            Stage65AcceptanceAction action,
            CancellationToken cancellationToken)
        {
            var page = await RequireSettingPageAsync(cancellationToken).ConfigureAwait(false);
            await WaitUntilAsync(
                () => OnDispatcherAsync(
                    () => page.CaptionSourceBox.IsEnabled,
                    cancellationToken),
                TimeSpan.FromSeconds(action.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            await OnDispatcherAsync(() =>
            {
                if (page.CaptionSourceBox.SelectedIndex == selectedIndex)
                    InvokeButton(page.RetryCaptionSourceButton);
                else
                    page.CaptionSourceBox.SelectedIndex = selectedIndex;
            }, cancellationToken).ConfigureAwait(false);
            await WaitUntilAsync(
                () => Task.FromResult(IsRequestedSourceSelectionComplete(selectedIndex)),
                TimeSpan.FromSeconds(action.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            var observed = await OnDispatcherAsync(() => new
            {
                ActiveStatus = page.CaptionSourceActiveStatus.Text,
                SourceFailure = page.CaptionSourceFailureStatus.Text,
                ProvisioningStatus = page.LocalAsrProvisioningStatus.Text,
                ProvisioningFailure = page.LocalAsrProvisioningFailure.Text,
                page.CaptionSourceBox.SelectedIndex
            }, cancellationToken).ConfigureAwait(false);
            Stage65AcceptanceTrace.Write("acceptance.settings.observed", observed);
        }

        private static bool IsRequestedSourceSelectionComplete(int selectedIndex)
        {
            var status = Translator.CaptionSourceApplicationStatus;
            if (status.IsSelectionInProgress)
                return false;

            if (selectedIndex == 1)
            {
                return status.ActiveSource == CaptionSourceKind.LocalAsr &&
                       status.SourceState == CaptionSourceState.Running;
            }

            return status.ActiveSource == CaptionSourceKind.WindowsLiveCaptions &&
                   status.SourceState == CaptionSourceState.Running ||
                   status.SourceState is CaptionSourceState.Faulted or
                       CaptionSourceState.Unavailable;
        }

        private async Task WaitForDisplayChangeAsync(
            string expected,
            Stage65AcceptanceAction action,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            System.ComponentModel.PropertyChangedEventHandler? handler = null;
            handler = (_, args) =>
            {
                if (args.PropertyName is not ("DisplayOriginalCaption" or
                    "OverlayOriginalCaption"))
                {
                    return;
                }

                if ((Translator.Caption?.DisplayOriginalCaption?.Contains(
                         expected, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    (mainWindow.OverlayWindow == null ||
                     (Translator.Caption?.OverlayOriginalCaption?.Contains(
                         expected, StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    completion.TrySetResult();
                }
            };
            await OnDispatcherAsync(
                () => Translator.Caption!.PropertyChanged += handler,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(action.TimeoutSeconds),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await OnDispatcherAsync(
                    () => Translator.Caption!.PropertyChanged -= handler,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static async Task WaitForHistoryAsync(
            Stage65AcceptanceAction action,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler() => completion.TrySetResult();
            Translator.TranslationLogged += Handler;
            try
            {
                await completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(action.TimeoutSeconds),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Translator.TranslationLogged -= Handler;
            }
        }

        private static async Task WaitForStatusAsync(
            Func<CaptionSourceApplicationStatus, bool> predicate,
            Stage65AcceptanceAction action,
            CancellationToken cancellationToken)
        {
            await WaitUntilAsync(
                () => Task.FromResult(predicate(
                    Translator.CaptionSourceApplicationStatus)),
                TimeSpan.FromSeconds(action.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
        }

        private Task CloseMainWindowAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = mainWindow.Dispatcher.BeginInvoke(
                () => SystemCommands.CloseWindow(mainWindow),
                DispatcherPriority.ApplicationIdle);
            return Task.CompletedTask;
        }

        private async Task<SettingPage> RequireSettingPageAsync(
            CancellationToken cancellationToken) =>
            await FindSettingPageAsync(cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("The real Settings page is not loaded.");

        private Task<SettingPage?> FindSettingPageAsync(
            CancellationToken cancellationToken) =>
            OnDispatcherAsync(
                () => FindVisualChild<SettingPage>(mainWindow),
                cancellationToken);

        private static T? FindVisualChild<T>(DependencyObject root)
            where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T typed)
                    return typed;
                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }
            return null;
        }

        private static void InvokeButton(ButtonBase button)
        {
            var peer = new ButtonAutomationPeer((System.Windows.Controls.Button)button);
            if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider provider)
                provider.Invoke();
            else
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        private Task OnDispatcherAsync(
            Action action,
            CancellationToken cancellationToken) =>
            mainWindow.Dispatcher.InvokeAsync(
                action, DispatcherPriority.Normal, cancellationToken).Task;

        private Task<T> OnDispatcherAsync<T>(
            Func<T> action,
            CancellationToken cancellationToken) =>
            mainWindow.Dispatcher.InvokeAsync(
                action, DispatcherPriority.Normal, cancellationToken).Task;

        private static async Task WaitUntilAsync(
            Func<Task<bool>> predicate,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!await predicate().ConfigureAwait(false))
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException(
                        $"Acceptance condition did not complete within {timeout}.");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
                return;

            lifetime.Dispose();
            try { await runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}
