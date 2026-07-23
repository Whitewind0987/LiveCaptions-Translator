# Current Status

## Repository state

- Branch: `feature/optional-live-captions`
- Baseline commit: `7a6fe4a5b294dacc4aa1b3666981d6c00dbcd183`
- Upstream repository: `SakiRinn/LiveCaptions-Translator`
- Completed stages:
  - Stage 0 environment and baseline verification
  - Stage 1 optional Windows Live Captions startup, verified on Windows 10
- Current status: Stage 1 is complete on Windows 10; Windows 11 runtime
  behavior has not yet been verified

## Environment

- .NET SDK: `10.0.201`
- Installed runtimes:
  - `Microsoft.NETCore.App 8.0.29`
  - `Microsoft.WindowsDesktop.App 8.0.29`
  - `Microsoft.NETCore.App 10.0.5`
  - `Microsoft.WindowsDesktop.App 10.0.5`

## Baseline verification

- `dotnet restore`: succeeds
- `dotnet build`: succeeds
- Baseline build warnings: 191

The 191 warnings are existing baseline warnings. Warning cleanup is outside
Stage 0, and these warnings are not addressed in this stage.

## Stage 1 implementation

Stage 1 removes the mandatory startup dependency on Windows Live Captions so
the application can start and remain usable on Windows 10.

### Changes

- `LiveCaptionsHandler.TryInitializeLiveCaptions()` protects the complete Live
  Captions setup: process launch, window discovery, window repair when needed,
  and hiding the window. It returns a structured result instead of throwing on
  failure.
- If complete initialization fails after starting a process, the handler tries
  to terminate only the process started by that attempt. Cleanup failures are
  included in the returned diagnostic and do not escape into the WPF process.
- Initial startup and the single controlled restart use the same complete
  initialization operation.
- `Translator.CaptionSourceUnavailable` flag distinguishes an unavailable
  caption source (e.g. Win10 without LiveCaptions.exe) from a window that was
  previously available and was unexpectedly closed. The property is externally
  read-only, and `Translator.CaptionSourceFailureReason` retains the most recent
  initialization failure reason.
- `Translator` static constructor no longer crashes when LiveCaptions.exe is
  absent. It records the unavailable state and initializes settings and caption
  state normally.
- `SyncLoop` displays `[WARNING] No caption source is available.` once when the
  source is unavailable, then idles safely.
- `TranslateLoop` does not attempt `Process.Start("LiveCaptions")` in a tight
  loop when the source is unavailable. For unexpected closure, it uses
  `TryLaunchLiveCaptions()` with a 2-second backoff. A failed restart sets
  `CaptionSourceUnavailable` to prevent further retry.
- `MainWindow.CheckForFirstUse()` guards `RestoreLiveCaptions` with a null
  window check.
- `SettingPage.CheckForFirstUse()` guards button text assignment with a null
  window check.
- `SettingPage.LiveCaptionsButton_click` already returned early on null window;
  unchanged beyond the existing null guard.
- `Translator.ApplyCaptionSourceUnavailableWarning()` applies the unavailable
  source warning consistently to the main caption and overlay state. Resuming
  from `[Paused]` restores the warning immediately when no caption source is
  available.

### Manual testing status

Windows 10 manual testing passed. The application starts and remains usable
without Windows Live Captions, displays the unavailable-source warning, restores
that warning immediately after pause/resume, does not repeatedly attempt to
launch Live Captions, and exits without leaving application or Live Captions
processes running.

Windows 11 runtime behavior has not yet been verified. No Windows 11 checks are
recorded as passed. See `docs/TESTING.md` for the detailed results.

### Local runtime files

The following root-level runtime files are ignored and were not committed:

- `/setting.json`
- `/translation_history.db`

### Known remaining Windows 10 incompatibility

Windows 10 still has no real-time caption source. The application shows a
warning and idles. A future stage will add local speech recognition. Stage 2
has not begun.

