# Testing

## Stage 0 baseline

### Git checks

- Branch: `feature/win10-asr-backend`
- `origin`: `Whitewind0987/LiveCaptions-Translator`
- `upstream`: `SakiRinn/LiveCaptions-Translator`
- Working tree before documentation: clean
- `HEAD`: synchronized with `upstream/master` at
  `7a6fe4a5b294dacc4aa1b3666981d6c00dbcd183`

### Build checks

- `dotnet restore`: passed
- `dotnet build`: passed
- Target framework: `net8.0-windows`
- Baseline warnings: 191

Warning cleanup is outside Stage 0. The warning count records the existing
baseline rather than an acceptance requirement for later stages.

### Windows 10 runtime check

- `dotnet run`: failed as expected on Windows 10
- Exception chain: `TypeInitializationException` for `Translator`, caused by a
  `Win32Exception` with error code 2
- Root cause: `Translator` static initialization calls
  `LiveCaptionsHandler.LaunchLiveCaptions()`, which calls
  `Process.Start("LiveCaptions")`; Windows 10 does not provide
  `LiveCaptions.exe`

Failure is the expected current baseline behavior. Stage 0 records the failure
and does not change runtime behavior.

### Acceptance result

- Environment is valid.
- The repository restores and builds.
- The Windows 10 incompatibility is reproduced and understood.
- No implementation changes were made.

## Stage 1 manual test checklist

Windows 10 manual testing is complete. Windows 11 runtime testing remains
pending and is not included in the Windows 10 acceptance result.

### Windows 10 checks

- Application opens without `TypeInitializationException`: **passed**
- Main window is usable: **passed**
- Settings page opens: **passed**
- History page opens: **passed**
- Overlay window opens and closes: **passed**
- `[WARNING] No caption source is available.` is displayed: **passed**
- Application remains open for at least two minutes: **passed**
- `LiveCaptions` process count remains 0 during six checks at 20-second
  intervals: **passed**
- Pausing displays `[Paused]`: **passed**
- Resuming immediately restores
  `[WARNING] No caption source is available.`: **passed**
- No repeated Live Captions launch attempts are observed: **passed**
- Application closes without leaving `LiveCaptionsTranslator` or
  `LiveCaptions` processes: **passed**

### Windows 10 acceptance result

Stage 1 is complete on Windows 10. The optional Windows Live Captions startup
behavior and unavailable-source pause/resume behavior passed manual verification.

### Windows 11 checks

All Windows 11 behavior checks are **pending** until tested on a Windows 11
system with Windows Live Captions available.

- Application opens on Windows 11: **pending**
- LiveCaptions process is launched and hidden: **pending**
- Captions are read from LiveCaptions window: **pending**
- Translations are produced: **pending**
- Overlay displays live captions and translations: **pending**
- First-use flow (settings page, LiveCaptions restored, welcome window): **pending**
- Show/Hide button in settings toggles LiveCaptions visibility: **pending**
- Unexpected LiveCaptions closure triggers controlled restart attempt: **pending**
- Application closes cleanly (LiveCaptions restored and killed): **pending**

### Build checks

- `dotnet restore`: **passed**
- `dotnet build`: **passed**
- Current build warning summary: 378 reported warnings, representing 189 unique
  source diagnostics reported once for the WPF temporary project and once for
  the main project
- New warnings introduced by the pause/resume warning restoration fix: none
- Stage 0 recorded baseline: 191 warnings

### Local runtime files

The following root-level local runtime files are ignored and were not committed:

- `/setting.json`
- `/translation_history.db`

## Future fixed audio fixtures

To be defined in a later implementation stage. No fixture results are recorded
in Stage 0.

## Future regression tests

To be defined and populated as implementation stages add independently testable
behavior. No regression-test results are recorded in Stage 0.

