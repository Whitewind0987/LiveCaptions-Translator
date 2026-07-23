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

- 193 passed
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

The remote-review regression set additionally covers synchronous `StopAsync`
from frame and status subscribers, stop waiting for an active publication,
terminal-stop races with active data processing, absence of frames after a
terminal status, one-shot cleanup after processing failure, repeated-failure
deduplication, buffer completion, original-plus-cleanup diagnostic retention,
stop failure followed by disposal, disposal failure retention, failed-start
cleanup aggregation, and independent capture/device cleanup attempts. Probe
regressions verify nonzero exit for unexpected `Unavailable`/`Faulted` closure,
zero exit for requested bounded or Ctrl+C-equivalent cancellation, WAV
finalization on failure, and final diagnostics being sampled only after service
stop/cleanup completes.

Native-thread hardening regressions use a deterministic fake runtime with a
dedicated callback thread whose disposal joins that thread. They verify that
`FrameProduced` runs on the managed dispatcher instead, a subscriber can call
synchronous `StopAsync`, the callback thread exits, runtime stop/disposal each
occur once, later handlers are suppressed, and no event follows stop. A second
mode emits data before runtime `StartAsync` returns and verifies that frame
publication waits for `Running` status publication and lifecycle-gate release.

Additional dispatcher tests cover ordered sequences, the fixed 32-notification
capacity and nonblocking overflow, stale queued-notification discard, terminal
status ordering, clean generation replacement after restart, and observed
dispatcher faults becoming typed service failures. Disposal regressions verify
that endpoint-provider, dispatcher, frame-buffer, and stop cleanup phases do not
skip one another, multiple failures remain diagnosable, repeated disposal stays
safe, and waiting frame consumers are released despite another phase failing.

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

Manual test date: **2026-07-23**

Windows 10 core real-audio acceptance passed with audible source audio on the
current default render endpoint:

- `--list` returned active render endpoints: **passed**
- The current default endpoint was available: **passed**
- The default-endpoint probe captured real audible source audio: **passed**
- Frames were produced and consumed: **passed**
- Dropped-frame count remained 0: **passed**
- RMS was greater than zero while audio was playing: **passed**
- Peak remained in the valid normalized PCM range: **passed**
- Normal bounded probe completion reached `Stopped` and exited with code 0:
  **passed**
- The generated normalized WAV was audible, played at normal speed, was not
  silent, and had no obvious severe distortion or clipping: **passed**
- Ctrl+C cancellation stopped cleanly with no reported failure: **passed**
- No probe process remained after exit: **passed**
- Ordinary WPF startup remained unchanged and did not begin audio capture:
  **passed**
- Settings output-device selector loaded and refreshed: **passed**
- History, Overlay, pause/resume, and normal shutdown remained functional:
  **passed**
- No application or `LiveCaptions` process regression was observed: **passed**

The successful real-capture run reported exactly:

```text
Final state: Stopped
Frames produced: 497
Frames consumed: 494
Frames dropped: 0
Captured duration: 9.88 s
RMS: 0.135257
Peak: 0.720886
Failure: none
WAV: C:\Users\YMXD\AppData\Local\Temp\livecaptions-stage3-probe.wav
ExitCode: 0
```

The three-frame difference between 497 produced and 494 consumed is a small
stop-boundary buffer remainder (60 ms at 20 ms per frame), not an overflow:
`Frames dropped` remained 0. The 494 consumed frames account for the reported
9.88-second captured WAV. Manual playback confirmed audible source audio,
normal playback speed, no obvious acceleration or slowdown, no obvious severe
distortion or clipping, and no silence.

The separate Ctrl+C cancellation run verified cancellation and cleanup only;
it is not an audio-content verification:

```text
Final state: Stopped
Frames produced: 0
Frames consumed: 0
Frames dropped: 0
Captured duration: 0.00 s
RMS: 0.000000
Peak: 0.000000
Failure: none
```

The following optional or edge checks were deliberately not run and remain
**pending**:

- Capture using a manually selected explicit endpoint ID: **pending**
- The default endpoint marker shown by `--list` is manually cross-checked
  against Windows: **pending**
- Settings identifies the resolved System default, beyond loading and
  refreshing the selector: **pending**
- A saved active endpoint remains selected after application restart:
  **pending**
- A missing saved endpoint falls back to System default with a clear
  diagnostic: **pending**
- Capture state is manually observed reaching `Running`: **pending**
- The selected endpoint and native input format are manually confirmed from
  probe output: **pending**
- The normalized format report is manually confirmed as 16,000 Hz, mono,
  signed PCM16 little-endian: **pending**
- Produced and consumed frame sequence continuity is manually confirmed:
  **pending**
- WAV header and duration are independently inspected, beyond successful
  playback and the reported 9.88-second duration: **pending**
- Repeated start/stop and a second start create clean new sessions: **pending**
- Audio/WAV resource locks are checked independently after exit: **pending**
- Endpoint disconnect or disable while capture is running produces a useful
  state and failure reason without crashing WPF: **pending**

## Stage 3 Windows 11 manual checklist

All Stage 3 Windows 11 audio checks are **pending**. Repeat the Windows 10
endpoint, capture, format, level, device-loss, restart, cancellation, and clean-
exit checks on Windows 11. The existing Stage 2B Windows Live Captions checklist
also remains pending and must not be inferred from Stage 3 build or unit tests.

## Later-stage fixtures and regression tests

Stage 3 uses generated audio fixtures only. Recorded speech fixtures, IPC,
workers, VAD, Whisper, caption generation, translation-version integration, and
recognition quality or latency tests belong to later explicitly approved stages.

## Stage 4 automated managed tests

Commands:

```powershell
dotnet restore
dotnet build
dotnet test tests/LiveCaptionsTranslator.Tests/LiveCaptionsTranslator.Tests.csproj
dotnet build tools/AudioCaptureProbe/AudioCaptureProbe.csproj
dotnet build tools/AsrWorkerProbe/AsrWorkerProbe.csproj
```

Current result:

- 283 passed
- 0 failed
- 0 skipped
- all existing 262 tests from before the normal-stop hardening are preserved
- all existing 193 Stage 3 tests are preserved
- no new C# or xUnit analyzer diagnostic originates in Stage 4 source or tests

Stage 4 coverage includes fixed envelope layout, RFC 4122 Guid ordering, typed
invalid-magic/major/minor/flag/type/size failures, optional unknown messages,
zero/duplicate/decreasing envelope sequences, fragmented
reads, truncated payloads, strict payload codecs, exact 700-byte audio frames,
caption-event mapping, and exact bidirectional shared golden vectors. In-process
two-pipe tests cover independent control/audio authentication, wrong audio
nonce/session/PID, second clients, no unauthenticated PCM exposure, distinct
control/audio/ready timeouts, control closure, typed `WorkerReportedError`, unknown
correlations, unexpected unsolicited messages, response identities, and
concurrent cleanup. Fake process/job/transport tests cover generation-aware
single coordinator-owned terminal cleanup, synchronously completed
process/heartbeat/transport monitor origins, immediate failure cleanup,
real progress diagnostics, and synchronous reentrant Stop from Starting, Ready,
Streaming, Faulted, Stopping, and Stopped notifications without deadlock or
post-stop events, stale Ready suppression after subscriber A synchronously stops
before subscriber B, and repeat-safe supervisor disposal. Buffer tests prove
deterministic completion after an empty blocked read, one/many/full 250-frame
queues, drop-oldest activity, concurrent completion/final read, and repeated
completion, with no blocked consumer after drain. Pump diagnostics tests cover
normal completion, source-completion observation, current/last sequence, and
owned cancellation. Pipeline tests cover continuously monitored pump failure,
capture/worker failure cleanup, progress-bounded normal drain, typed source-
completion and transport-write stalls, cancellation/join without a false
`cancellation/join failed`, caller cancellation during mandatory cleanup, and final-summary
validation, typed root-cause retention, `AudioStreamEnd` ordering, and matching
end/summary totals. Incomplete drains send neither `AudioStreamEnd` nor
`StopAudioStream`, and every stop path proves the pump joined. Initial-gap tests count sequence 2/3 drops before the first
sent frame. All earlier ordering, buffer, and repeated-session regressions are
preserved.

The normal xUnit suite uses fake processes/transports and in-process pipes. It
does not launch the C++ worker, open WASAPI, start WPF, access the network, call
translation providers, or read runtime settings/history files.

## Stage 4 native build and tests

The installed Visual Studio CMake is not on PATH. The equivalent full-path
commands were run using CMake 4.2.3-msvc3, Visual Studio 18 2026, MSVC
19.50.35728.0, and Windows SDK 10.0.26100.0 targeting Windows 10.0.19045:

```powershell
cmake --preset windows-x64
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release
```

Results:

- x64 configure: **passed**
- `LiveCaptionsAsrWorker.exe` Release build with `/W4 /WX`: **passed**
- native protocol-test Release build with `/W4 /WX`: **passed**
- CTest: **1 passed, 0 failed**
- new native warnings: **0**
- ARM64 preset is documented but was not built locally: **pending**

Native coverage includes fragmented envelope and payload input, invalid UTF-8,
major/minor/flag validation, required/optional unknown types, independent
sequence ordering, correlation and trailing-byte rejection, audio lifecycle,
and candidate-before-commit stream statistics. Gap tests enforce `sequence
delta * 320` sample-index movement, include first-frame gaps relative to the
declared initial sequence, prove wrong initial sample indices leave state
unchanged and allow recovery, and validate matching/mismatched end totals.

Shared vectors are in:

```text
protocol/v1/test-vectors/protocol-v1.hex
```

The 13 vectors cover `WorkerHello`, `AudioPipeHello`, `AudioPipeAccepted`,
`HostAccept`, `WorkerReady`, `StartAudioStream`, one exact `AudioFrame`,
`AudioStreamEnd`, `AudioProgress`, `Error`, `CaptionEvent`, `Shutdown`, and
`ShutdownAcknowledged`. C# and C++ tests both decode every expected field,
encode the expected value, and compare the exact bytes with the fixture.

## Stage 4 synthetic cross-process acceptance

The real x64 C++ worker was exercised through the developer probe on Windows
10. No system audio was opened.

Baseline synthetic command:

```powershell
dotnet run --project tools/AsrWorkerProbe/AsrWorkerProbe.csproj -- --worker native/AsrWorker/build/windows-x64/bin/LiveCaptionsAsrWorker.exe --synthetic --duration 5
```

Result: **passed**. The host launched one owned native process, authenticated
both random pipes to the same session/nonce/PID, negotiated protocol 1.0 with
only `ProtocolV1` and `NormalizedPcmSink`, sent 250 frames / 160,000 PCM bytes,
and received a matching 250-frame summary with 0 gaps and 0 invalid frames. A
real Pong was observed, shutdown was acknowledged, forced termination was not
used, exit code was 0, cleanup failures were empty, and no worker remained.

Explicit restart command:

```powershell
dotnet run --project tools/AsrWorkerProbe/AsrWorkerProbe.csproj -- --worker native/AsrWorker/build/windows-x64/bin/LiveCaptionsAsrWorker.exe --synthetic --duration 3 --restart
```

Result: **passed**. Exactly one explicit restart created a different worker
session and different pipe identities. Across both sessions the probe generated
200 frames and the workers reported 200 received, 0 gaps, 0 heartbeat failures,
successful shutdown acknowledgment, no forced termination, exit code 0, and no
cleanup failures. No automatic retry loop or orphan process was observed.

Controlled-exit command:

```powershell
dotnet run --project tools/AsrWorkerProbe/AsrWorkerProbe.csproj -- --worker native/AsrWorker/build/windows-x64/bin/LiveCaptionsAsrWorker.exe --synthetic --duration 3 --controlled-exit
```

Result: **passed**. Terminating only the supervisor-owned worker became typed
`WorkerExited`, completed the single stored cleanup without a pipe/job/process/
monitor/disposal failure, did not require a cleanup-phase forced termination,
did not hang, and left no worker process.

Deterministic Ctrl+C-equivalent command:

```powershell
dotnet run --project tools/AsrWorkerProbe/AsrWorkerProbe.csproj -- --worker native/AsrWorker/build/windows-x64/bin/LiveCaptionsAsrWorker.exe --synthetic --duration 30 --cancel-after-ms 1500
```

Result: **passed** with exit code 0 and `Requested cancellation completed
cleanly.` No worker process remained. Interactive keyboard Ctrl+C remains part
of manual acceptance.

Deterministic slow-worker drain-barrier command:

```powershell
dotnet run --project tools/AsrWorkerProbe/AsrWorkerProbe.csproj -- --worker native/AsrWorker/build/windows-x64/bin/LiveCaptionsAsrWorker.exe --synthetic --duration 5 --slow-worker
```

Result: **passed**. The native reader was deliberately delayed while the host
published a 250-frame burst, forcing an audio-pipe backlog. The explicit end
barrier drained before the control stop summary: 250 frames and 160,000 PCM
bytes were sent and received, with 0 gaps, 0 heartbeat failures, acknowledged
shutdown, no forced termination, exit code 0, no cleanup failures, and no
remaining worker.

## Stage 4 developer probe

Synthetic, slow-worker barrier, restart, controlled-exit, and deterministic cancellation modes are
shown above. Real Stage 3 audio uses the production capture service, buffer,
worker transport, and coordinator without WAV transport:

```powershell
dotnet run --project tools/AsrWorkerProbe/AsrWorkerProbe.csproj -- --worker <path-to-LiveCaptionsAsrWorker.exe> --audio --device default --duration 10
```

An explicit endpoint ID may replace `default`. The compiled native worker,
CMake build tree, PDB files, and probe artifacts are ignored and must not be
committed.

## Stage 4 Windows 10 real-audio manual checklist

The default-device normal-stop path was rerun on Windows 10 on 2026-07-24 with
the previously verified audible Stage 3 WAV played through the selected output.
The final result was 500 capture frames produced/consumed, 0 buffered, 0 dropped,
500 pump and transport frames, 320,000 PCM bytes, and a matching 500-frame worker
summary with 0 gaps and 0 invalid frames. The pump phase was `Completed`, source
completion was observed, `PumpJoined` was true, owned cancellation was false,
heartbeat failures were 0, shutdown was acknowledged, forced termination was
false, exit code was 0, and both pipeline and worker failures/cleanup failures
were empty. No `LiveCaptionsAsrWorker` or `LiveCaptionsTranslator` process
remained after validation.

- Native worker executable starts from the explicit path: **passed**
- Both random pipes connect and authenticated handshake succeeds: **passed**
- Heartbeat remains healthy during real capture: **passed**
- Real Stage 3 default-device audio streams for ten seconds: **passed**
- Capture frames sent match worker frames received: **passed** (500 / 500)
- No unexpected sequence gaps occur: **passed** (0)
- Stage 3 bounded-buffer drops remain controlled: **passed** (0)
- `AudioStreamStopped` summary matches host diagnostics: **passed**
- Interactive Ctrl+C shuts down capture, pump, pipes, and worker: **pending**
- No `LiveCaptionsAsrWorker` process remains: **passed**
- Controlled worker termination during real capture becomes a typed failure
  without hanging WPF-side code: **passed**
- Explicit restart during the real-audio probe creates a new worker session:
  **passed**
- No worker remains after every validation probe exit: **passed**

The real-audio controlled-exit probe captured 149 frames, consumed 124, pumped
and transported 123 frames / 78,720 PCM bytes, and received 50 frames / 32,000
bytes of worker progress before intentionally terminating only the supervisor-
owned worker. The pipeline finished `Faulted` with typed `WorkerExited`;
capture stopped, the pump joined, forced termination was not used, cleanup and
disposal failures were empty, no owned PID remained, the final process check
was empty, and the probe returned 0.

The real-audio restart probe is an explicit bounded two-session acceptance, not
a seamless mid-stream restart. Session 1 used PID 3652 and session
`2453313c-849a-4257-b999-b660506bb2cb`; all capture, pump, transport, and worker
totals were 248 frames / 158,720 PCM bytes. After a complete clean stop/drain,
session 2 used PID 16000 and session
`cf3f9deb-5446-4657-8ee7-cb37fe64d743`; all four totals were 249 frames /
159,360 PCM bytes. Both sessions received real audio, had 0 drops, gaps,
invalid frames, heartbeat failures, forced terminations, cleanup failures, or
disposal failures, and ended `Stopped` with acknowledged graceful shutdown and
exit code 0. No worker remained and the probe returned 0.

Interactive Ctrl+C was attempted from the non-interactive validation host, but
the host could not reliably deliver a genuine console Ctrl+C event; those
attempts are not counted as passing. The deterministic cancellation probe still
passes. Its real-audio run returned 0 after 341 capture, pump, transport, and
worker-summary frames / 218,240 PCM bytes, with 0 buffered, dropped, gap, or
invalid frames; capture and pipeline ended `Stopped`, the pump joined after
source completion, graceful shutdown succeeded, forced termination was false,
cleanup/disposal failures were empty, and no PID remained. An interactive
terminal run remains required for the Ctrl+C item.

Ordinary WPF startup remains unchanged and is not a Stage 4 worker owner. It
must continue to start neither audio capture nor the worker.

## Stage 4 Windows 11 manual checklist

All equivalent Stage 4 native-worker, two-pipe handshake, heartbeat, real-audio
streaming, summary, failure, restart, Ctrl+C, and orphan-process checks are
**pending** on Windows 11. No Windows 11 Stage 4 result is inferred from Windows
10 synthetic validation.

## Stage 4 limitations

Stage 4 has no VAD, Whisper, CUDA, models, recognition, generated captions,
production `ICaptionSource`, Translator integration, translation changes, or
packaging integration. Stage 5 has not begun.

