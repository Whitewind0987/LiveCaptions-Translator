# Current Status

## Repository state

- Branch: `feature/vad-whisper-worker`
- Stage 3 starting commit: `ce36855781824597de3e7a8e2901345967f9bd82`
- Stage 4 starting commit: `04e4f2c95ac98a8c32dbb7ae34b1a679a52835ad`
- Upstream repository: `SakiRinn/LiveCaptions-Translator`
- Completed stages:
  - Stage 0 environment and baseline verification
  - Stage 1 optional Windows Live Captions startup, verified on Windows 10
  - Stage 2A source-independent contracts and event-ordering core
  - Stage 2B Windows Live Captions source adapter and production integration
  - Stage 3 WPF audio-capture foundation implementation, automated tests, and
    Windows 10 core real-audio acceptance
  - Stage 4 native worker-process, versioned IPC, and normalized-audio transport
    foundation implementation and automated cross-process validation
- Current status: Stage 5 CPU recognition implementation is present on the
  working branch. Deterministic managed/native tests, the real-model silence
  fixture, and the real-model known-speech cross-process fixture passed on
  Windows 10. Final acceptance is pending full rebuild/test after the last
  dispatcher, cancellation, diagnostics and post-roll edits plus real-WASAPI
  and recognition Ctrl+C runs. NuGet TLS failure and the execution environment's
  exhausted elevated-command allowance blocked that rerun. Stage 5 is therefore
  not marked complete.
- Next stage: finish the remaining Stage 5 acceptance only. Stage 6 has not
  begun and requires explicit approval after Stage 5 acceptance.

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

The combined test suite currently passes 193 tests with 0 failed and 0 skipped,
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

The remaining native-thread review issue is now hardened: the NAudio callback
only copies, normalizes, buffers, and queues a lightweight immutable-frame
notification. External `FrameProduced` handlers execute serially on a tracked
managed dispatcher backed by a bounded 32-notification channel, activated only
after `Running` publication and lifecycle-gate release. Stop/fault invalidates
queued generation-tagged notifications; synchronous subscriber stop cannot
deadlock a native capture-thread Join. Service disposal now attempts stop,
endpoint-provider cleanup, dispatcher cleanup, frame-buffer cleanup, and gate
cleanup independently and aggregates all phase failures.

NAudio is pinned to 2.3.0 and is used only at the Windows audio boundary. It and
its six 2.3.0 managed transitive packages are MIT licensed, add no new native
binaries, and occupy approximately 705 KiB as compressed NuGet packages in the
local cache.

Windows 10 core real-audio acceptance passed on 2026-07-23. The default-endpoint
probe captured audible source audio, produced 497 frames, consumed 494 frames,
dropped 0 frames, wrote 9.88 seconds of normalized audio with RMS 0.135257 and
peak 0.720886, reached `Stopped`, reported no failure, and exited with code 0.
The three unconsumed frames were a 60 ms stop-boundary buffer remainder rather
than dropped audio. Manual WAV playback had normal speed, audible source audio,
and no obvious severe distortion or clipping. Endpoint listing, default-device
availability, bounded completion, Ctrl+C cleanup, no remaining probe process,
unchanged WPF startup, Settings selector load/refresh, History, Overlay,
pause/resume, normal shutdown, and absence of application or Live Captions
process regressions were also verified.

The following optional or edge Windows 10 checks remain pending because they
were deliberately not run:

- capture using a manually selected explicit endpoint ID;
- cross-checking the default marker and reported endpoint/input formats;
- confirming Settings' resolved-System-default label;
- persistence of a saved active endpoint across application restart;
- missing saved endpoint fallback to System default;
- manually observing `Running` and frame-sequence continuity;
- independent WAV-header/duration and post-exit resource-lock checks;
- repeated start/stop with a clean new capture session;
- endpoint disconnect or disable while capture is running.

All Windows 11 Stage 3 runtime acceptance checks remain pending. Speech
recognition is not implemented; the following section records the completed
Stage 4 transport foundation.

## Stage 4 implementation

Stage 4 adds protocol 1.0, two random current-user named pipes, independent
nonce/session/PID authentication for both pipes, explicit capability
negotiation, normalized PCM transport, heartbeat, typed terminal failures,
bounded live progress diagnostics, explicit restart, and generation-aware
owned-process cleanup. `AsrWorkerSupervisor`, `NamedPipeWorkerTransport`,
`AudioFramePump`, and `AudioWorkerPipeline` keep transport, process, audio, and
coordination responsibilities separate. The Stage 3 250-frame buffer remains
the sole PCM backlog; completed publication dispatchers are pruned between
repeated sessions.

The separately built C++20 `LiveCaptionsAsrWorker.exe` uses Win32 pipes and
parent monitoring, validates and discards normalized frames, reports bounded
progress/final summaries, and shuts down cleanly. A host Job Object uses
kill-on-close. C# and C++ both exactly decode and re-encode all shared textual
golden vectors.
No third-party Stage 4 package, license, native DLL, or model binary was added;
the compiled worker is a local ignored project artifact.

The remaining Stage 4 review blockers are hardened: terminal monitor origins
signal a separately owned coordinator instead of entering cleanup themselves;
status publication is generation/state-version guarded; lifecycle disposal is
joined and repeat-safe; and the audio pipe now has an explicit validated
`AudioStreamEnd` drain barrier. Initial source gaps are counted from the stream's
declared sequence, and managed envelope minor/flag validation matches native.
The real-WASAPI normal-stop regression is also hardened: buffer completion is
signaled independently from frame availability, pump phase/progress/completion/
cancellation diagnostics are explicit, and normal stop drains with finite
progress and stall bounds before sending stream-completion control messages.
Owned cancellation joins a genuinely stalled pump without being misreported as
the original failure, and caller cancellation cannot skip mandatory cleanup.

Automated managed tests pass 293 tests with 0 failed and 0 skipped, preserving
all 283 tests from the immediately previous committed result and all 193 Stage
3 tests. The x64 worker and
native test executable build with MSVC `/W4 /WX`; CTest passes 1 of 1. Real C++
cross-process probes pass a five-second 250-frame synthetic stream with a real
heartbeat, one explicit restart (200/200 aggregate frames, zero gaps), typed
controlled worker exit with complete cleanup, and deterministic cancellation.
A deterministic slow-worker/backlog probe also sent and received all 250 frames
and 160,000 PCM bytes with zero gaps, proving that Stop waits for the audio end
barrier rather than racing queued audio. No worker remained after the probe runs.

On Windows 10 on 2026-07-24, a ten-second default-device run with the previously
verified audible Stage 3 WAV produced, drained, sent, and summarized all 500
frames / 320,000 PCM bytes with zero drops, gaps, or invalid frames. The pump
observed source completion, reached `Completed`, required no owned cancellation,
and joined before `AudioStreamEnd`; shutdown was acknowledged with exit code 0,
no cleanup failure, and no remaining worker. Controlled worker failure during
real capture and explicit real-audio restart now pass: controlled exit retains
typed `WorkerExited`, joins capture/pump cleanup without forced termination or
cleanup failures, and leaves no worker; the restart check is an explicit
bounded two-session test (248 frames then 249) with different session IDs and
observed PIDs 3652 and 16000, exact per-session capture/pump/transport/worker
totals, and clean drains. PID inequality is diagnostic rather than mandatory:
Windows may reuse a positive PID after the first worker has fully exited. This
does not claim seamless mid-stream restart.

Interactive Windows 10 Ctrl+C acceptance also passes. It produced 651 frames,
dropped 28 through the bounded buffer, and consistently consumed, pumped,
transported, and summarized 623 frames; source and worker-summary gaps both
matched the 28 drops. The pump completed and joined after source completion
without owned cancellation, the worker exited gracefully with code 0, forced
termination was not used, cleanup/disposal failures were empty, and no owned
PID remained. This is bounded-drop accounting and clean-interruption evidence,
not zero-loss evidence; the separate 500/500 normal-stop run remains the strict
zero-drop acceptance. All Windows 11 Stage 4 runtime checks remain pending.
Ordinary WPF startup is unchanged and does not construct capture or worker
infrastructure.

## Stage 5 CPU recognition implementation and acceptance status

Stage 5 adds an opt-in CPU recognition build of the same native worker. The
existing transport-only preset remains model-free. The recognition preset pins
whisper.cpp v1.8.6 (`23ee035`), Silero VAD v6.2.1 (`7e30209`) and ONNX Runtime
CPU 1.23.0, loads both explicitly supplied absolute model paths before
`WorkerReady`, and advertises VAD, Whisper and caption-production capabilities
without CUDA. Models, dependency trees, runtime DLLs, fixtures and build output
remain ignored local files.

The worker owns stateful 512-sample Silero windows over existing 320-sample
frames, bounded speech segments, one serialized Whisper inference thread,
latest-wins Partial work, mandatory Final work, generation-based stale-result
rejection, gap Reset behavior, and Reset/Partial/Committed/Final wire events.
Transport and pipeline expose guarded notifications for future Stage 6 without
routing anything into the application. Ordinary WPF startup remains unchanged.

### Managed validation

The complete managed suite passed 312 tests with 0 failed and 0 skipped,
preserving all 293 previous tests. The
`NormalStopDeliversCaptionEventsPublishedByWorkerDuringEndStopBarrier`
regression was strengthened: 50 consecutive runs, 0 failures.
`git diff --check` passed. Production timeout values were not weakened.
`ForwardProgressExtendsDrainBeyondOneStallWindow` changed only deterministic
test timing.

### Normal-stop caption lifecycle fix

The normal-stop caption order was corrected. The correct non-reentrant order
is:

1. Capture stops and the audio pump drains.
2. `EndAudioStreamAsync` and `StopAudioStreamAsync` complete.
3. The worker's Committed/Final caption events accepted before the stop
   barrier remain valid.
4. `DrainAndDisableCaptionDeliveryAsync` waits until all already accepted
   caption publications finish.
5. In the same `stateLock` critical section where `captionPublications` is
   observed as zero:
   - `captionDeliveryEnabled` becomes `false`;
   - `captionGeneration` advances;
   - `captionTransport` is captured and cleared.
6. The transport event handler is detached outside the lock.
7. `SetStreamingAsync(false)` and `supervisor.StopAsync` run.
8. Remaining cleanup completes.

Caption delivery is not disabled before accepted publications drain. A
reentrant `StopAsync` from inside a caption subscriber invalidates immediately
and does not wait for itself. Abnormal cleanup invalidates immediately. The
zero-count check and delivery invalidation are atomic, fixing the final TOCTOU
race.

### Native and real-model validation

Both transport-only and recognition CTest presets passed all native test
executables during Stage 5 finalization. Repository-owned native targets passed
`/W4 /WX`. Upstream whisper.cpp warnings are not repository-owned warnings.

Real two-second silence fixture produces only Reset.

The pinned speech WAV fixture produced the following deterministic results:

- 399 frames, 255,360 bytes
- 160 bytes final-frame padding
- 0 gaps, 0 invalid frames
- graceful true, forced false, exit code 0
- Gate-accepted lifecycle: Reset 1; Partial 2; Committed 3; Final 4; Partial 5;
  Committed 6; Final 7
- Segment 1: `This is a local speech recognition test.`
- Segment 2: `The worker shoot produced structured caption events.`

The actual model-dependent transcript is recorded verbatim; "should" was
recognized as "shoot".

### Windows 10 real-WASAPI recognition runs

An earlier strict real-WASAPI normal-stop run, before the final caption-drain
TOCTOU and ordering-only corrections, achieved the following:

- 517 produced, 517 consumed, 0 dropped
- 0 gaps, 0 invalid, non-empty Final
- graceful true, forced false, exit code 0
- no cleanup/disposal failure or process residue

This result remains recorded as evidence from the immediately preceding
lifecycle revision.

Exact final ordering revision normal-stop reruns:

Run 1:
- 600 produced, 517 consumed, 83 dropped
- pump gaps 83, worker summary gaps 83
- 0 invalid, non-empty Final delivered
- graceful true, forced false, pipeline Stopped
- no cleanup/disposal failures
- probe exit code 1 because strict zero-drop acceptance was not met

Run 2:
- 599 produced, 573 consumed, 26 dropped
- pump gaps 26, worker summary gaps 26
- 0 invalid
- graceful true, forced false, pipeline Stopped
- no cleanup/disposal failures
- probe exit code 1 because strict zero-drop acceptance was not met

The final source corrections affect stop-time caption publication ordering,
not the live capture buffer. Neither final-revision run is zero-loss.

### Final Ctrl+C run (exact final ordering revision)

- Requested cancellation completed cleanly
- 741 produced, 613 consumed, 128 dropped
- exact accounting: 741 = 613 + 128
- pump source gaps 128, worker summary gaps 128
- 0 invalid frames
- pump Completed and joined
- source completion observed
- owned cancellation false
- graceful true, forced false
- worker exit code 0, worker PID none
- pipeline Stopped
- no worker/pipeline failure
- no cleanup or disposal failures

This is accepted as a clean bounded-drop interruption, not a zero-loss run.

### Stage 4 regression

All Stage 4 synthetic and transport-only probe matrix tests were rerun during
Stage 5 finalization and passed.

### Status

Stage 5 implementation is complete. Windows 10 model, protocol, caption
lifecycle, deterministic WAV, cancellation, cleanup and ownership validation
are complete. A strict zero-drop real-WASAPI normal-stop confirmation on the
exact final ordering revision remains pending. The prior 517/517 strict run
remains recorded as evidence from the immediately preceding lifecycle revision.
Windows 11 remains pending. Stage 6 has not begun. Recognition remains
developer-probe/worker functionality and is not yet the production
`ICaptionSource`.

