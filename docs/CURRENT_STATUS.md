# Current Status

## Repository state

- Branch: `feature/wasapi-audio-capture`
- Stage 3 starting commit: `ce36855781824597de3e7a8e2901345967f9bd82`
- Upstream repository: `SakiRinn/LiveCaptions-Translator`
- Completed stages:
  - Stage 0 environment and baseline verification
  - Stage 1 optional Windows Live Captions startup, verified on Windows 10
  - Stage 2A source-independent contracts and event-ordering core
  - Stage 2B Windows Live Captions source adapter and production integration
  - Stage 3 WPF audio-capture foundation implementation and automated tests
- Current status: Stage 2 is complete and accepted on Windows 10. Stage 3 is
  implemented and passes automated tests; real-device runtime acceptance on
  Windows 10 and Windows 11 remains pending
- Next stage: finish Stage 3 manual audio-capture acceptance. Stage 4 must not
  begin without explicit approval

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
warning and idles. A future stage will add local speech recognition.

## Stage 2A implementation

Stage 2A adds source-independent captioning foundations under `src/captioning/`:

- immutable and validated caption-event schema version 1;
- `ICaptionSource`, source states, immutable status, and start-result models;
- `CaptionEventGate` for session, sequence, segment, revision, and lifecycle
  ordering, with accepted and highest-observed sequence state tracked
  separately;
- immutable `CaptionTranslationRequest` identity derived only from committed or
  final events;
- a focused `net8.0-windows` xUnit test project with 55 passing tests.

## Stage 2B implementation

Stage 2B integrates the source contracts into production:

- `WindowsLiveCaptionsSource` implements `ICaptionSource` using a narrow typed
  Windows runtime adapter over the existing low-level `LiveCaptionsHandler`;
- `CaptionSourceHost` subscribes before startup, validates all events through
  `CaptionEventGate`, and retains one thread-safe latest accepted snapshot;
- `Translator` owns no UI Automation object and performs no direct
  `LiveCaptionsHandler` calls;
- `App` explicitly starts one production source and owns loop cancellation,
  source stop, and asynchronous disposal during shutdown;
- MainWindow and SettingPage use the optional typed native-window capability;
- managed snapshot processing preserves the existing preprocessing,
  sentence-extraction, idle, sync, translation-queue, display, and overlay
  behavior;
- application shutdown now observes every background loop and still attempts
  source stop and disposal after cancellation, loop, or earlier cleanup
  failures, retaining phase-specific diagnostics;
- source state, session generation, and processable snapshot are exposed as one
  atomic Host value; any non-running state invalidates the stored snapshot so
  stale text cannot overwrite warnings or enter the translation queue;
- context clearing now takes effect before overlay history expansion by using
  an effective context count of zero for that tick;
- a focused `net8.0-windows` xUnit suite now has 100 passing tests.

Known transitional behavior: `WindowsLiveCaptionsSource` emits changed complete
raw snapshots as `Partial` events only. It deliberately emits no `Committed` or
`Final` event; the existing Translator commit and idle heuristics still decide
when text enters the translation queue. Translation-version rejection remains a
later-stage responsibility.

Stage 1 remains complete on Windows 10. Stage 2B manual runtime acceptance
passed on Windows 10 on 2026-07-23, including unavailable-source startup,
pause/resume warning restoration, two-minute stability, seven process checks,
no repeated Live Captions launch attempt, and clean process shutdown. Stage 2
is complete and accepted on Windows 10.

Windows 11 runtime behavior remains unverified and no Windows 11 checks are
recorded as passed.

## Stage 3 implementation

Stage 3 adds the WPF-owned audio-capture foundation without connecting it to
ordinary application startup or caption production:

- asynchronous enumeration of active Windows render endpoints, multimedia-
  default resolution, saved-device selection, and explicit fallback reasons;
- an `AudioOutputDeviceId` setting and non-blocking settings-page selector;
- a narrow NAudio 2.3.0 WASAPI loopback runtime with explicit resource ownership;
- streaming float32 and PCM16/24/32 decoding, multi-channel downmix, stateful
  resampling, and exact 16 kHz mono PCM16 little-endian output;
- immutable 20 ms frames (320 samples, 640 bytes) with capture-session identity,
  sequence, sample index, and monotonic time;
- a bounded 250-frame drop-oldest buffer with exact overflow diagnostics and
  cancellation-aware reads;
- serialized start/stop/restart/disposal, stale-callback rejection, typed
  unavailable/faulted states, retained failure reasons, and deterministic
  cleanup;
- a developer-only command-line probe for endpoint listing, bounded capture,
  RMS/peak reporting, and optional normalized WAV output;
- deterministic tests using synthetic audio and fake runtimes only.

The combined test suite currently passes 182 tests with 0 failed and 0 skipped,
preserving all 100 Stage 2 tests. The main application build retains the existing
378 reported warnings (189 unique diagnostics); Stage 3 introduces no new main-
project warnings. A probe-project build may report the same 189 diagnostics
while rebuilding its main-project reference; no warning originates in the probe
or new Stage 3 audio source.

Remote-review hardening now ensures that no frame or status subscriber runs
while an internal lock or lifecycle gate is held. Active callback/publication
work participates in a generation-aware stop barrier, including synchronous
subscriber-initiated stops. Processing failures and unexpected native stops use
one stored cleanup operation that is joined by stop and disposal, publishes one
terminal state, and retains the original failure plus all independent stop,
detach, capture-disposal, and endpoint-disposal failures. The developer probe
now finalizes WAV output and joins service cleanup on every path, reports final
post-cleanup diagnostics, and returns nonzero for unexpected capture, WAV, or
cleanup failures.

NAudio is pinned to 2.3.0 and is used only at the Windows audio boundary. It and
its six 2.3.0 managed transitive packages are MIT licensed, add no new native
binaries, and occupy approximately 705 KiB as compressed NuGet packages in the
local cache.

Real Windows 10 and Windows 11 endpoint enumeration, capture, device switching,
device loss, bounded-duration probe, and clean process/resource shutdown remain
manual Stage 3 acceptance work. Speech recognition is not implemented and Stage
4 has not begun.

