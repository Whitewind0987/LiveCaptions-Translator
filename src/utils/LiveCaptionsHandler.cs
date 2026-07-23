using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.utils
{
    internal enum LiveCaptionsInitializationFailureCategory
    {
        Unavailable,
        Faulted
    }

    internal sealed record LiveCaptionsInitializationResult(
        bool Success,
        AutomationElement? Window,
        int? ProcessId,
        LiveCaptionsInitializationFailureCategory? FailureCategory,
        string? ErrorMessage);

    public static class LiveCaptionsHandler
    {
        public static readonly string PROCESS_NAME = "LiveCaptions";

        private static AutomationElement? captionsTextBlock = null;

        internal static LiveCaptionsInitializationResult TryInitializeLiveCaptions(
            CancellationToken cancellationToken = default)
        {
            Process? startedProcess = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                captionsTextBlock = null;
                KillAllProcessesByPName(PROCESS_NAME);
                cancellationToken.ThrowIfCancellationRequested();
                startedProcess = Process.Start(PROCESS_NAME);
                if (startedProcess == null)
                {
                    return CreateInitializationFailure(
                        null,
                        LiveCaptionsInitializationFailureCategory.Faulted,
                        "Failed to start LiveCaptions process.");
                }

                AutomationElement? window = null;
                for (int attemptCount = 0;
                     window == null || window.Current.ClassName.CompareTo("LiveCaptionsDesktopWindow") != 0;
                     attemptCount++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    window = FindWindowByPId(startedProcess.Id);
                    if (attemptCount > 10000)
                    {
                        return CreateInitializationFailure(
                            startedProcess,
                            LiveCaptionsInitializationFailureCategory.Faulted,
                            "Failed to find LiveCaptions window after launching.");
                    }
                }

                FixLiveCaptions(window);
                HideLiveCaptions(window);
                return new LiveCaptionsInitializationResult(
                    true, window, startedProcess.Id, null, null);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                return CreateInitializationFailure(
                    startedProcess,
                    LiveCaptionsInitializationFailureCategory.Unavailable,
                    "LiveCaptions.exe is not available on this system.");
            }
            catch (Win32Exception ex)
            {
                return CreateInitializationFailure(
                    startedProcess,
                    LiveCaptionsInitializationFailureCategory.Faulted,
                    $"Failed to initialize LiveCaptions: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                var cleanupError = TryTerminateStartedProcess(startedProcess);
                if (!string.IsNullOrWhiteSpace(cleanupError))
                    Debug.WriteLine(cleanupError);
                throw;
            }
            catch (Exception ex)
            {
                return CreateInitializationFailure(
                    startedProcess,
                    LiveCaptionsInitializationFailureCategory.Faulted,
                    $"Failed to initialize LiveCaptions: {ex.Message}");
            }
            finally
            {
                try
                {
                    startedProcess?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to dispose LiveCaptions process handle: {ex.Message}");
                }
            }
        }

        private static LiveCaptionsInitializationResult CreateInitializationFailure(
            Process? startedProcess,
            LiveCaptionsInitializationFailureCategory failureCategory,
            string errorMessage)
        {
            var cleanupError = TryTerminateStartedProcess(startedProcess);
            if (!string.IsNullOrEmpty(cleanupError))
                errorMessage = $"{errorMessage} {cleanupError}";

            return new LiveCaptionsInitializationResult(
                false, null, null, failureCategory, errorMessage);
        }

        private static string? TryTerminateStartedProcess(Process? startedProcess)
        {
            if (startedProcess == null)
                return null;

            try
            {
                if (!startedProcess.HasExited)
                {
                    startedProcess.Kill();
                    if (!startedProcess.WaitForExit(2000))
                        return "Cleanup timed out while terminating the started LiveCaptions process.";
                }
            }
            catch (Exception ex)
            {
                return $"Cleanup failed for the started LiveCaptions process: {ex.Message}";
            }

            return null;
        }

        internal static string? TryRestoreAndTerminate(
            AutomationElement? window,
            int? processId)
        {
            var failures = new List<string>();

            if (window != null)
            {
                try
                {
                    RestoreLiveCaptions(window);
                }
                catch (Exception ex)
                {
                    failures.Add($"Failed to restore the Live Captions window: {ex.Message}");
                }

                if (!processId.HasValue)
                {
                    try
                    {
                        nint hWnd = new((long)window.Current.NativeWindowHandle);
                        WindowsAPI.GetWindowThreadProcessId(hWnd, out int discoveredProcessId);
                        processId = discoveredProcessId;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"Failed to identify the Live Captions process: {ex.Message}");
                    }
                }
            }

            if (processId.HasValue)
            {
                try
                {
                    using var process = Process.GetProcessById(processId.Value);
                    if (!process.HasExited)
                    {
                        process.Kill();
                        if (!process.WaitForExit(2000))
                            failures.Add("Timed out while terminating the Live Captions process.");
                    }
                }
                catch (ArgumentException)
                {
                    // The source-owned process has already exited.
                }
                catch (Exception ex)
                {
                    failures.Add($"Failed to terminate the Live Captions process: {ex.Message}");
                }
            }

            captionsTextBlock = null;
            return failures.Count == 0 ? null : string.Join(" ", failures);
        }

        public static void HideLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_MINIMIZE);
            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle | WindowsAPI.WS_EX_TOOLWINDOW);
        }

        public static void RestoreLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle & ~WindowsAPI.WS_EX_TOOLWINDOW);
            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_RESTORE);
            WindowsAPI.SetForegroundWindow(hWnd);
        }

        public static void FixLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);

            RECT rect;
            if (!WindowsAPI.GetWindowRect(hWnd, out rect))
                throw new Exception("Unable to get the window rectangle of LiveCaptions!");
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            int x = rect.Left;
            int y = rect.Top;

            bool isSuccess = true;
            if (x < 0 || y < 0 || width < 100 || height < 100)
                isSuccess = WindowsAPI.MoveWindow(hWnd, 800, 600, 600, 200, true);
            if (!isSuccess)
                throw new Exception("Failed to fix LiveCaptions!");
        }

        public static string GetCaptions(AutomationElement window)
        {
            if (captionsTextBlock == null)
                captionsTextBlock = FindElementByAId(window, "CaptionsTextBlock");
            try
            {
                return captionsTextBlock?.Current.Name ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                captionsTextBlock = null;
                throw;
            }
        }

        private static AutomationElement? FindWindowByPId(int processId)
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
        }

        public static AutomationElement? FindElementByAId(
            AutomationElement window, string automationId, CancellationToken token = default)
        {
            try
            {
                PropertyCondition condition = new PropertyCondition(
                    AutomationElement.AutomationIdProperty, automationId);
                return window.FindFirst(TreeScope.Descendants, condition);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }

        public static void PrintAllElementsAId(AutomationElement window)
        {
            var treeWalker = TreeWalker.RawViewWalker;
            var stack = new Stack<AutomationElement>();
            stack.Push(window);

            while (stack.Count > 0)
            {
                var element = stack.Pop();
                if (!string.IsNullOrEmpty(element.Current.AutomationId))
                    Console.WriteLine(element.Current.AutomationId);

                var child = treeWalker.GetFirstChild(element);
                while (child != null)
                {
                    stack.Push(child);
                    child = treeWalker.GetNextSibling(child);
                }
            }
        }

        public static bool ClickSettingsButton(AutomationElement window)
        {
            var settingsButton = FindElementByAId(window, "SettingsButton");
            if (settingsButton != null)
            {
                var invokePattern = settingsButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                if (invokePattern != null)
                {
                    invokePattern.Invoke();
                    return true;
                }
            }
            return false;
        }

        private static void KillAllProcessesByPName(string processName)
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
                return;
            foreach (Process process in processes)
            {
                process.Kill();
                process.WaitForExit();
            }
        }
    }
}
