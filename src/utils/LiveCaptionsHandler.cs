using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.utils
{
    public static class LiveCaptionsHandler
    {
        public static readonly string PROCESS_NAME = "LiveCaptions";

        private static AutomationElement? captionsTextBlock = null;

        public static (bool Success, AutomationElement? Window, string? ErrorMessage) TryInitializeLiveCaptions()
        {
            Process? startedProcess = null;

            try
            {
                KillAllProcessesByPName(PROCESS_NAME);
                startedProcess = Process.Start(PROCESS_NAME);
                if (startedProcess == null)
                    return CreateInitializationFailure(null, "Failed to start LiveCaptions process.");

                AutomationElement? window = null;
                for (int attemptCount = 0;
                     window == null || window.Current.ClassName.CompareTo("LiveCaptionsDesktopWindow") != 0;
                     attemptCount++)
                {
                    window = FindWindowByPId(startedProcess.Id);
                    if (attemptCount > 10000)
                        return CreateInitializationFailure(
                            startedProcess, "Failed to find LiveCaptions window after launching.");
                }

                FixLiveCaptions(window);
                HideLiveCaptions(window);
                return (true, window, null);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                return CreateInitializationFailure(
                    startedProcess, "LiveCaptions.exe is not available on this system.");
            }
            catch (Win32Exception ex)
            {
                return CreateInitializationFailure(
                    startedProcess, $"Failed to initialize LiveCaptions: {ex.Message}");
            }
            catch (Exception ex)
            {
                return CreateInitializationFailure(
                    startedProcess, $"Failed to initialize LiveCaptions: {ex.Message}");
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

        public static AutomationElement LaunchLiveCaptions()
        {
            var (success, window, errorMessage) = TryInitializeLiveCaptions();
            if (!success)
                throw new Exception(errorMessage ?? "Failed to initialize LiveCaptions!");
            return window!;
        }

        private static (bool Success, AutomationElement? Window, string ErrorMessage)
            CreateInitializationFailure(Process? startedProcess, string errorMessage)
        {
            var cleanupError = TryTerminateStartedProcess(startedProcess);
            if (!string.IsNullOrEmpty(cleanupError))
                errorMessage = $"{errorMessage} {cleanupError}";

            return (false, null, errorMessage);
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

        public static void KillLiveCaptions(AutomationElement window)
        {
            // Search for process
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            WindowsAPI.GetWindowThreadProcessId(hWnd, out int processId);
            var process = Process.GetProcessById(processId);

            // Kill process
            process.Kill();
            process.WaitForExit();
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

        private static AutomationElement FindWindowByPId(int processId)
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
