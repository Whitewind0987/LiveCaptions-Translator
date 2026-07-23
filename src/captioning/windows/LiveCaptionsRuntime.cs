using System.Windows;
using System.Windows.Automation;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.captioning.windows
{
    internal sealed class LiveCaptionsRuntime : ILiveCaptionsRuntime
    {
        private readonly SemaphoreSlim operationGate = new(1, 1);
        private readonly object stateLock = new();
        private AutomationElement? window;
        private int? ownedProcessId;
        private bool disposed;

        public bool IsWindowAvailable
        {
            get
            {
                lock (stateLock)
                    return window != null;
            }
        }

        public bool? IsWindowVisible
        {
            get
            {
                AutomationElement? currentWindow;
                lock (stateLock)
                    currentWindow = window;

                if (currentWindow == null)
                    return null;

                try
                {
                    return currentWindow.Current.BoundingRectangle != Rect.Empty;
                }
                catch (ElementNotAvailableException)
                {
                    ClearWindow(currentWindow);
                    return null;
                }
                catch
                {
                    return null;
                }
            }
        }

        public async Task<LiveCaptionsRuntimeInitializationResult> InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();

                var result = await Task.Run(
                    () => LiveCaptionsHandler.TryInitializeLiveCaptions(cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (!result.Success || result.Window == null || !result.ProcessId.HasValue)
                {
                    return LiveCaptionsRuntimeInitializationResult.Failed(
                        result.FailureCategory == LiveCaptionsInitializationFailureCategory.Unavailable
                            ? LiveCaptionsRuntimeFailureCategory.Unavailable
                            : LiveCaptionsRuntimeFailureCategory.Faulted,
                        result.ErrorMessage ?? "Live Captions initialization failed without a reason.");
                }

                lock (stateLock)
                {
                    window = result.Window;
                    ownedProcessId = result.ProcessId;
                }

                return LiveCaptionsRuntimeInitializationResult.Started();
            }
            finally
            {
                operationGate.Release();
            }
        }

        public async Task<LiveCaptionsSnapshotReadResult> ReadSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return LiveCaptionsSnapshotReadResult.Cancelled();
            }

            try
            {
                if (disposed)
                    return LiveCaptionsSnapshotReadResult.Cancelled();

                AutomationElement? currentWindow;
                lock (stateLock)
                    currentWindow = window;

                if (currentWindow == null)
                    return LiveCaptionsSnapshotReadResult.WindowLost("The Live Captions window is unavailable.");

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = currentWindow.Current.Name;
                    var text = LiveCaptionsHandler.GetCaptions(currentWindow);
                    return string.IsNullOrWhiteSpace(text)
                        ? LiveCaptionsSnapshotReadResult.NoText()
                        : LiveCaptionsSnapshotReadResult.Snapshot(text);
                }
                catch (OperationCanceledException)
                {
                    return LiveCaptionsSnapshotReadResult.Cancelled();
                }
                catch (ElementNotAvailableException)
                {
                    ClearWindow(currentWindow);
                    return LiveCaptionsSnapshotReadResult.WindowLost(
                        "The Live Captions window was unexpectedly closed.");
                }
                catch (Exception ex)
                {
                    return LiveCaptionsSnapshotReadResult.Faulted(
                        $"Failed to read Live Captions text: {ex.Message}");
                }
            }
            finally
            {
                operationGate.Release();
            }
        }

        public Task<CaptionWindowControlResult> ShowAsync(CancellationToken cancellationToken = default) =>
            ControlWindowAsync(show: true, cancellationToken);

        public Task<CaptionWindowControlResult> HideAsync(CancellationToken cancellationToken = default) =>
            ControlWindowAsync(show: false, cancellationToken);

        private async Task<CaptionWindowControlResult> ControlWindowAsync(
            bool show,
            CancellationToken cancellationToken)
        {
            try
            {
                await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return CaptionWindowControlResult.Failed(
                    CaptionWindowControlFailure.Cancelled, "The window operation was cancelled.");
            }

            try
            {
                AutomationElement? currentWindow;
                lock (stateLock)
                    currentWindow = window;

                if (currentWindow == null)
                {
                    return CaptionWindowControlResult.Failed(
                        CaptionWindowControlFailure.Unavailable,
                        "The Live Captions window is unavailable.");
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (show)
                        LiveCaptionsHandler.RestoreLiveCaptions(currentWindow);
                    else
                        LiveCaptionsHandler.HideLiveCaptions(currentWindow);
                    return CaptionWindowControlResult.Completed();
                }
                catch (OperationCanceledException)
                {
                    return CaptionWindowControlResult.Failed(
                        CaptionWindowControlFailure.Cancelled, "The window operation was cancelled.");
                }
                catch (ElementNotAvailableException)
                {
                    ClearWindow(currentWindow);
                    return CaptionWindowControlResult.Failed(
                        CaptionWindowControlFailure.Unavailable,
                        "The Live Captions window is unavailable.");
                }
                catch (Exception ex)
                {
                    return CaptionWindowControlResult.Failed(
                        CaptionWindowControlFailure.Faulted,
                        $"Failed to {(show ? "show" : "hide")} the Live Captions window: {ex.Message}");
                }
            }
            finally
            {
                operationGate.Release();
            }
        }

        public async Task<LiveCaptionsRuntimeCleanupResult> ShutdownAsync(
            CancellationToken cancellationToken = default)
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AutomationElement? currentWindow;
                int? processId;
                lock (stateLock)
                {
                    currentWindow = window;
                    processId = ownedProcessId;
                    window = null;
                    ownedProcessId = null;
                }

                if (currentWindow == null && !processId.HasValue)
                    return LiveCaptionsRuntimeCleanupResult.Cleaned();

                var error = await Task.Run(
                    () => LiveCaptionsHandler.TryRestoreAndTerminate(currentWindow, processId),
                    CancellationToken.None).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(error)
                    ? LiveCaptionsRuntimeCleanupResult.Cleaned()
                    : LiveCaptionsRuntimeCleanupResult.Failed(error);
            }
            catch (Exception ex)
            {
                return LiveCaptionsRuntimeCleanupResult.Failed(
                    $"Failed to clean up Live Captions: {ex.Message}");
            }
            finally
            {
                operationGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;

            await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            disposed = true;
            operationGate.Dispose();
        }

        private void ClearWindow(AutomationElement expectedWindow)
        {
            lock (stateLock)
            {
                if (ReferenceEquals(window, expectedWindow))
                    window = null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(LiveCaptionsRuntime));
        }
    }
}
