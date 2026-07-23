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

## Stage 2A automated tests

Command:

```powershell
dotnet test tests/LiveCaptionsTranslator.Tests/LiveCaptionsTranslator.Tests.csproj
```

Result:

- 55 passed
- 0 failed
- 0 skipped

Coverage includes caption-event construction invariants, accepted-versus-
observed sequence ordering, rejected-sequence retention, foreign-session
isolation, reset behavior, segment and revision ordering, lifecycle progression,
finalized segment protection, rejected-event state isolation, and
committed/final translation-request identity.

The tests are deterministic and do not launch Windows Live Captions, access UI
Automation, create windows, call translation APIs, access the network, or use
local settings or history files.

The main application project excludes `tests/**/*.cs` from its default recursive
compile items. Test dependencies are referenced only by the test project.

Stage 2A itself did not change runtime behavior. Stage 2B integration is
documented below. Stage 1 remains verified on Windows 10; final Stage 2B manual
runtime verification remains pending on Windows 10 and Windows 11.

## Stage 2B automated tests

Command:

```powershell
dotnet test tests/LiveCaptionsTranslator.Tests/LiveCaptionsTranslator.Tests.csproj
```

Result:

- 100 passed
- 0 failed
- 0 skipped

The final suite preserves all 55 Stage 2A tests and adds deterministic coverage
for `WindowsLiveCaptionsSource`, typed initialization/read/cleanup outcomes,
ordered Reset and changed-snapshot Partial events, duplicate starts, subscriber
isolation, controlled restart, cancellation during restart, stop/disposal,
cleanup diagnostics, optional native-window control, `CaptionSourceHost` gate
integration, atomic source-state/latest-snapshot reads, and managed legacy
caption processing. Pre-acceptance regressions additionally verify that a
faulted loop cannot skip source stop or disposal, all shutdown failures remain
diagnosable, `Restarting`/`Unavailable`/`Faulted` states invalidate stale
snapshots through the real Host-to-processor path, delayed inactive-source
events cannot reactivate text, a new-session Reset plus Partial recovers
normally, and context clearing precedes overlay history expansion while normal
expansion remains intact.

All Stage 2B source tests use fake runtimes, fake delays, and fake caption
sources. They do not launch `LiveCaptions.exe`, access real UI Automation,
create visible WPF windows, call translation providers, use the network, or read
local settings/history runtime files. The injected restart delay avoids a real
two-second wait.

## Stage 2B Windows 10 manual checklist

Manual test date: **2026-07-23**

All Stage 2B Windows 10 checks passed:

- Application opens without `TypeInitializationException`: **passed**
- Generic unavailable-source warning appears: **passed**
- Settings opens: **passed**
- History opens: **passed**
- Overlay opens and closes: **passed**
- Pause displays `[Paused]`: **passed**
- Resume immediately restores
  `[WARNING] No caption source is available.`: **passed**
- Application remains open and stable for at least two minutes: **passed**
- No `LiveCaptions` process exists on Windows 10: **passed**
- No repeated Live Captions launch attempts occur: **passed**
- Application exits cleanly: **passed**
- No `LiveCaptionsTranslator` process remains after exit: **passed**

Seven process checks were recorded at 20-second intervals while the application
remained open:

| Time | `LiveCaptions` | `LiveCaptionsTranslator` |
|---|---:|---:|
| 13:28:04 | 0 | 1 |
| 13:28:24 | 0 | 1 |
| 13:28:44 | 0 | 1 |
| 13:29:04 | 0 | 1 |
| 13:29:24 | 0 | 1 |
| 13:29:44 | 0 | 1 |
| 13:30:04 | 0 | 1 |

After normal application exit, both `LiveCaptions` and
`LiveCaptionsTranslator` process counts were 0. Stage 2B is accepted on Windows
10.

## Stage 2B Windows 11 manual checklist

All checks below are **pending** on Windows 11 with Windows Live Captions
available.

- Live Captions starts: **pending**
- Native window is hidden: **pending**
- Show/Hide setting works: **pending**
- Captions reach the main window: **pending**
- Captions reach the overlay: **pending**
- Translation still occurs: **pending**
- History still logs: **pending**
- Unexpected Live Captions closure triggers one restart: **pending**
- Restart creates a new source session: **pending**
- Application exit restores and terminates the source-owned process: **pending**
- No orphan process remains: **pending**

## Stage 3 automated tests

Commands:

```powershell
dotnet restore
dotnet build
dotnet test tests/LiveCaptionsTranslator.Tests/LiveCaptionsTranslator.Tests.csproj
dotnet build tools/AudioCaptureProbe/AudioCaptureProbe.csproj
```

Current result:

- 166 passed
- 0 failed
- 0 skipped
- all existing 100 Stage 2 tests are preserved
- main application: 378 reported warnings, 0 errors; the warnings represent the
  existing 189 source diagnostics reported once for the WPF temporary project
  and once for the main project
- developer probe: builds successfully; when its main-project reference is
  rebuilt, the command reports the existing 189 main-project diagnostics and 0
  errors, with no warning originating in probe or Stage 3 audio source
- new Stage 3 warnings: none

Stage 3 tests cover saved/default/missing endpoint resolution, enumeration
ordering and diagnostics, settings compatibility and persistence, float32 and
PCM16/24/32 decoding, mono and multi-channel downmix, clipping and byte order,
48 kHz and 44.1 kHz resampling, callback-boundary equivalence, reset behavior,
exact frame boundaries, session/sequence/sample-index reset, immutable payloads,
bounded drop-oldest behavior, cancellation and completion, start/stop/restart,
duplicate and concurrent lifecycle calls, expected and unexpected native stops,
device-unavailable and faulted outcomes, stale callback rejection, subscriber
isolation, cleanup, RMS/peak calculation, WAV finalization, and bounded probe
duration.

All audio samples are generated in memory and all service tests use fake
endpoint providers and capture runtimes. Automated tests do not enumerate or
open real audio devices, start the WPF application, access the network, launch
Windows Live Captions, write repository runtime settings/history files, or
leave probe processes or WAV artifacts.

## Stage 3 developer probe

List active render endpoints and the resolved system default:

```powershell
dotnet run --project tools/AudioCaptureProbe/AudioCaptureProbe.csproj -- --list
```

Capture the default endpoint for ten seconds and report normalized-frame count,
drops, duration, RMS, peak, endpoint/input-format details, and failures:

```powershell
dotnet run --project tools/AudioCaptureProbe/AudioCaptureProbe.csproj -- --duration 10 --device default
```

Optionally write the normalized 16 kHz mono PCM16 stream as a diagnostic WAV:

```powershell
dotnet run --project tools/AudioCaptureProbe/AudioCaptureProbe.csproj -- --duration 10 --device default --wav probe.wav
```

`--device` also accepts an endpoint ID returned by `--list`. The probe is a
developer tool and is excluded from the WPF application's compile items. A WAV
path is used only when explicitly supplied; generated WAV files are diagnostic
artifacts and must not be committed.

## Stage 3 Windows 10 manual checklist

All checks are **pending** until run interactively on Windows 10 while known
audio is playing:

- `--list` returns active render endpoints without blocking: **pending**
- The current default endpoint is correctly marked: **pending**
- Settings lists active endpoints and identifies the resolved system default:
  **pending**
- A saved active endpoint resolves and remains selected after application
  restart: **pending**
- A missing saved endpoint is visible and falls back safely with a clear
  diagnostic: **pending**
- Ordinary WPF startup remains unchanged and starts no audio capture: **pending**
- With clearly audible speech or video playing, the default-endpoint probe runs
  for at least ten seconds: **pending**
- Capture state reaches `Running`: **pending**
- The selected endpoint and native input format are reported: **pending**
- Normalized format reports 16,000 Hz, mono, signed PCM16 little-endian:
  **pending**
- Frames are produced and consumed and their sequences are continuous:
  **pending**
- Dropped-frame count remains 0 under normal conditions: **pending**
- RMS is greater than zero while audio is playing: **pending**
- Peak remains in the valid normalized PCM range: **pending**
- The optional normalized WAV has a valid header, expected duration, and is
  audible without gross speed, distortion, or channel errors: **pending**
- Repeated start/stop and a second start create clean new sessions: **pending**
- Stopping the probe and pressing Ctrl+C both end capture promptly and cleanly:
  **pending**
- Exit leaves no `AudioCaptureProbe` or `LiveCaptionsTranslator` process and no
  locked audio/WAV resource: **pending**
- Disconnecting or disabling the selected endpoint produces a useful state and
  failure reason without crashing WPF, where practical to test: **pending**
- Existing Settings, History, overlay, pause/resume, and Windows 10 unavailable-
  source behavior remain unchanged: **pending**

## Stage 3 Windows 11 manual checklist

All Stage 3 Windows 11 audio checks are **pending**. Repeat the Windows 10
endpoint, capture, format, level, device-loss, restart, cancellation, and clean-
exit checks on Windows 11. The existing Stage 2B Windows Live Captions checklist
also remains pending and must not be inferred from Stage 3 build or unit tests.

## Later-stage fixtures and regression tests

Stage 3 uses generated audio fixtures only. Recorded speech fixtures, IPC,
workers, VAD, Whisper, caption generation, translation-version integration, and
recognition quality or latency tests belong to later explicitly approved stages.

