# Planned Architecture

## Status

This document records the planned architecture for the Windows 10/11-compatible
fork. Stage 2 implements and integrates the source-independent caption
contracts and Windows Live Captions adapter. Stage 3 implements the WPF-owned
WASAPI loopback capture, normalization, framing, and bounded-buffer foundation.
Stage 4 implements the separately built native worker-process boundary,
versioned named-pipe IPC, supervision, and normalized-audio transport. Stage 5
adds an explicitly configured CPU recognition build while preserving the
model-free transport-only worker. Production caption-source integration remains
future Stage 6 work.

## Objective

The project will capture audio played by arbitrary Windows applications, perform
local real-time speech recognition, and feed recognized captions into the
existing translation and display workflow. The result must support Windows 10
and Windows 11 without requiring the operating-system Windows Live Captions
feature.

## Current upstream behavior

The upstream .NET 8 WPF application starts the Windows `LiveCaptions` process,
finds its window through UI Automation, reads caption text from that window, and
passes the text to the application's translation, overlay, settings, and history
features.

This startup path is incompatible with Windows 10. `Translator` static
initialization calls `LiveCaptionsHandler.LaunchLiveCaptions()`, which calls
`Process.Start("LiveCaptions")`. Windows 10 does not provide
`LiveCaptions.exe`, so process creation fails with `Win32Exception` error code 2.
The failure is then exposed as a `TypeInitializationException` for `Translator`.

## Target architecture

The final system has two process boundaries:

1. The existing .NET 8 WPF application remains the user-facing and coordinating
   process. It owns WASAPI loopback audio capture, PCM normalization, bounded
   audio buffering, and sends normalized PCM to the ASR worker.
2. A separate native ASR worker receives normalized PCM audio from the WPF
   process, runs local speech recognition (Silero VAD + whisper.cpp), and
   publishes caption events.

The processes communicate over Windows named pipes using a versioned local IPC
protocol. The IPC design supports control messages, lifecycle messages,
normalized PCM audio, worker status, errors, and caption events. Protocol
messages must be explicitly versioned so that either process can detect
incompatible peers and fail predictably.

## WPF application responsibilities

The WPF process remains responsible for:

- the main UI and overlay captions;
- translation API integration;
- settings and translation history;
- WASAPI loopback system-audio capture;
- playback-device enumeration and selection;
- conversion of captured audio to normalized 16 kHz mono PCM;
- bounded audio buffering;
- sending normalized PCM to the ASR worker through versioned local IPC;
- ASR model and native runtime management;
- starting, monitoring, stopping, and, where appropriate, restarting the ASR
  worker;
- validating IPC protocol versions and caption-event ordering;
- submitting caption text for translation and rejecting stale translation
  results.

The WPF process must not directly load the final CUDA speech-recognition runtime.

## ASR worker responsibilities

The future native worker will be responsible for:

- receiving configuration and lifecycle commands from the WPF process;
- receiving normalized PCM audio from the WPF process via named pipe;
- applying Silero VAD to identify speech regions;
- running `whisper.cpp` with optional CUDA acceleration and CPU fallback;
- emitting ordered partial, committed, final, and reset caption events;
- reporting readiness, recoverable errors, fatal errors, and capability details;
- releasing native audio, model, and accelerator resources when it exits.

## Audio path

The planned audio path is:

```
arbitrary application audio
→ Windows audio engine
→ [WPF] WASAPI loopback capture
→ [WPF] resampling/channel conversion
→ [WPF] normalized 16 kHz mono PCM
→ [IPC] versioned named pipe
→ [ASR worker] VAD (Silero)
→ [ASR worker] whisper.cpp
```

The WPF process owns capture and normalization, producing a stable input format
independent of the endpoint's native sample rate, channel layout, or sample
representation. The ASR worker receives only normalized PCM and owns VAD and
inference. Capture, conversion, buffering, IPC, VAD, and recognition must be
testable as separate stages.

## Stage 3 WPF audio capture foundation

Stage 3 implements the part of the audio path that belongs in the WPF process:

```text
active render endpoint
→ NAudio WASAPI loopback callback
→ streaming format decoder and channel downmix
→ stateful linear resampling
→ 16 kHz mono PCM16 little-endian
→ 20 ms immutable frames
→ bounded drop-oldest buffer
```

The Windows-specific boundary is limited to endpoint enumeration and
`WasapiLoopbackCaptureRuntime`. The rest of the pipeline depends on narrow
managed contracts and is deterministic under unit tests. Endpoint selection
uses the saved render-endpoint ID when it is currently active; otherwise it
falls back to the current multimedia default and retains an explicit fallback
diagnostic. The settings page enumerates active render endpoints asynchronously,
shows the resolved system default, keeps a missing saved endpoint visible, and
persists only a stable endpoint ID. Enumeration alone never starts capture.

The streaming normalizer supports interleaved IEEE float32 and PCM16, PCM24,
and PCM32 input. It preserves incomplete input blocks across callbacks, averages
all channels to mono using floating-point arithmetic, clamps the result, and
uses a stateful linear interpolator so splitting the same input into different
callback sizes produces the same normalized sample stream. Reset discards all
decoder and resampler remainder.

The normalized contract is fixed at 16,000 samples per second, one channel,
signed PCM16 little-endian. `AudioFrameAssembler` emits immutable 20 ms frames:
320 samples and 640 bytes per frame, with a capture-session ID, sequence number,
starting sample index, and monotonic timestamp. Partial frame data is retained
between callbacks and discarded on stop or restart. A successful restart creates
a new session and resets both sequence and sample-index state.

`BoundedAudioFrameBuffer` has a fixed default capacity of 250 frames (five
seconds). When full it drops the oldest frame, never exceeds the bound, and
records the exact drop count. Reads are cancellation-aware and completion wakes
waiting readers. Overflow is available through diagnostics rather than a
per-frame UI/status notification, avoiding a status-event storm.

`AudioCaptureService` is the single lifecycle owner for endpoint resolution,
one native runtime, normalization, framing, buffering, cancellation, and
cleanup. Starts and stops are serialized; duplicate starts are predictable;
stale callbacks from an old runtime are ignored; subscriber exceptions are
isolated; expected and unexpected native stops are distinguished; and failure
states retain explicit reasons. Stop and asynchronous disposal independently
attempt native cleanup and are safe to repeat. Device loss becomes an explicit
unavailable or faulted status rather than an exception escaping a callback.

Callback processing is serialized per capture session, but frame and status
subscribers are always invoked after internal locks and lifecycle gates have
been released. A generation-aware activity/publication barrier invalidates new
work when stop or failure begins and lets cleanup join work already in flight.
It also permits a subscriber to call `StopAsync` synchronously without waiting
on its own publication; the invalidated generation prevents any later handler,
frame, or stale callback from being published after that stop completes.

The native WASAPI callback never executes `FrameProduced` subscribers. After
copying callback-owned bytes, serialized normalization, frame assembly, and the
normal bounded PCM-buffer write, the callback only attempts to enqueue a
lightweight notification containing the existing immutable frame reference.
A tracked single-reader managed dispatcher publishes those notifications in
sequence through a separate bounded channel with capacity 32; a full
notification channel drops the newest notification without blocking capture or
duplicating the five-second PCM payload buffer. The PCM frame buffer remains the
authoritative future IPC-consumer path.

Each dispatcher is created paused for one capture generation. Callbacks during
`Starting` may fill only that bounded notification channel; activation happens
after the `Running` status has been published and `StartAsync` has released its
lifecycle gate. Stop or fault invalidates and completes the dispatcher, rejects
queued old-generation notifications, and normally joins its tracked runner. A
subscriber synchronously stopping from the dispatcher flow does not join its
own current publication; native capture cleanup can therefore join the already-
returned WASAPI thread, while generation checks prevent another handler or
frame notification after the synchronous stop returns.

The first normalization failure or unexpected native stop reserves one terminal
cleanup operation. That stored operation completes the frame buffer, rejects
later callbacks, waits for active processing/publication where required, stops
and disposes the runtime, clears streaming remainder, aggregates the original
failure with every cleanup failure, and publishes exactly one terminal state.
`StopAsync` and `DisposeAsync` join that same operation. Native cleanup clears
owned fields first and independently attempts stop, both handler detachments,
capture disposal, and endpoint disposal, so one cleanup error cannot suppress a
later action or its diagnostic.

Service disposal independently attempts stop, endpoint-provider disposal,
dispatcher completion/disposal, frame-buffer completion/disposal, and final
lifecycle-gate disposal. Failures are aggregated after every phase has been
attempted, and waiting frame consumers are released even if another disposal
phase fails.

Ordinary application startup deliberately does not construct or start
`AudioCaptureService`. Stage 3 exposes the capture foundation and a separate
developer probe, but production capture will begin only when a future IPC
consumer has explicit lifecycle ownership. Stage 3 does not add IPC, a worker,
VAD, Whisper, models, caption production, or translation changes.

NAudio is pinned at 2.3.0 for the Windows WASAPI and Core Audio boundary. It is
MIT licensed and supports the application's Windows-targeted .NET runtime. The
package brings the managed NAudio.Asio, NAudio.Core, NAudio.Midi, NAudio.Wasapi,
NAudio.WinForms, and NAudio.WinMM 2.3.0 packages. It adds no new native binary;
the seven compressed NuGet packages total approximately 705 KiB in the local
package cache. Only the Windows adapter exposes NAudio device or capture types.

## Stage 4 worker process and IPC foundation

Stage 4 establishes the final process boundary without adding recognition. The
WPF-side host creates one random full-duplex control pipe and one independent
random audio pipe for each session, then launches exactly one
explicitly located `LiveCaptionsAsrWorker.exe`. A random authentication nonce is
inherited through a private environment value rather than exposed on the
command line. Control `WorkerHello` and independent audio `AudioPipeHello`
proofs bind both pipes to the same worker-session Guid, nonce, and owned process
ID before any PCM is exposed, then negotiate protocol 1.0 plus the fixed Stage
3 audio format.

The binary little-endian protocol uses a fixed envelope, bounded payloads,
strict UTF-8, RFC 4122 Guid ordering, per-direction message sequences, typed
failures, and explicit correlation IDs. It covers lifecycle, readiness,
heartbeat, progress, errors, shutdown, normalized audio, and the existing
caption-event schema. Shared C# and C++ golden vectors lock the wire layout. The
complete byte contract is documented in `docs/IPC_PROTOCOL.md`.

`AsrWorkerSupervisor` owns one process, two pipes, heartbeat, bounded process
output, and one generation-aware stored terminal coordinator/cleanup. Each
monitor only signals the first terminal request and returns; the separately
tracked coordinator then joins all monitor origins without self-await. Status
publication uses immutable generation/state-version tokens, rechecks before and
after every subscriber, and lets reentrant lifecycle changes stop stale delivery
to later subscribers. Stop waits in-flight publication except its own current
callback. Process, heartbeat, transport, protocol, and worker-error failures join
the same cleanup and retain
their original typed cause plus all cleanup failures. It exposes explicit restart but never loops
automatically. A Windows Job Object with kill-on-close prevents an owned worker
from surviving an abnormal host exit; the worker independently monitors the
supplied parent PID. Graceful shutdown is bounded and forced termination targets
only the owned process tree. Process, Job Object, pipe, cancellation source, and
long-lived task ownership remain explicit.

`AudioFramePump` consumes `AudioCaptureService.FrameBuffer` directly. It
preserves order, reports source gaps (including a gap before the first sent
frame) and byte/frame totals, drains buffered data
on normal completion, and performs one sequential audio-pipe write per frame.
Its thread-safe diagnostics distinguish waiting, writing, normal completion,
owned cancellation, and fault phases, including current/last sequence and
whether source completion was observed. Normal pipeline stop uses finite
progress- and stall-bounded draining: advancing frame/byte totals extend the
stall window, while a stalled phase is retained as a typed failure before the
owned token is canceled and joined. `AudioStreamEnd` and `StopAudioStream` are
sent only after the pump observes normal source completion. Frame-buffer
completion uses a separate state-change signal from the authoritative bounded
queue, so queued frames drain before closure without a semaphore sentinel.
It creates no second PCM queue: Stage 3's fixed 250-frame drop-oldest buffer
remains the sole audio backpressure boundary. Completed retired Stage 3 frame-
publication dispatchers are pruned across repeated capture sessions so restart
testing cannot grow their retained list without bound.

The C++20 worker is built separately by CMake. It connects both Win32 pipes,
authenticates, responds to heartbeat and lifecycle commands, validates the
fixed 16 kHz mono PCM16 frame stream, maintains bounded counters, publishes
progress every 50 frames, waits for and validates an explicit audio-pipe
`AudioStreamEnd` drain barrier before returning the final summary, and discards PCM. Native
envelope sequences, correlations, complete payload consumption, stream state,
and sequence-scaled sample indices are validated before statistics mutate.
Synchronous pipe readers are woken with `CancelSynchronousIo` through retained
thread handles and every thread is joined with a bounded policy. Stage 4
adds no VAD, Whisper, CUDA, models, recognition, caption production, or ASR
native DLL loaded by WPF. Ordinary WPF
startup does not start audio capture or the worker.

## Stage 5 CPU recognition worker

`LCT_ENABLE_RECOGNITION` composes the existing worker with a repository-owned
recognition core plus narrow Silero ONNX and whisper.cpp adapters. The original
`windows-x64` preset remains independent of recognition dependencies; the
`windows-x64-recognition` preset requires explicit pinned local paths, statically
links whisper.cpp, dynamically links the official CPU `onnxruntime.dll` beside
the worker, and disables CUDA and host-specific CPU optimization. The WPF
process loads neither native runtime.

The audio reader converts PCM16 with `sample / 32768.0f`, accumulates sequential
320-sample frames into exact 512-sample VAD windows, and preserves the model's
separate 64-sample context plus `[2,1,128]` recurrent state. It never runs
Whisper. One owned inference thread serializes all Whisper calls with one
replaceable pending Partial and one mandatory pending Final. Requests carry
capture session, recognition generation, segment, revision and absolute sample
bounds; publication rechecks these identities so stale work is discarded.

Central defaults are threshold 0.50, minimum speech 250 ms, endpoint silence
500 ms, pre-roll 200 ms, post-roll 100 ms, maximum segment 20 seconds, minimum
Partial audio 800 ms and Partial cadence 1,000 ms. A source gap invalidates
provisional work, discards the active segment, resets VAD context/remainder and
emits Reset without fabricating silence. Normal drain processes accepted PCM,
flushes the incomplete VAD window, finalizes active speech, waits for mandatory
inference and publishes captions before `AudioStreamStopped`.

Each stream uses its capture Guid as caption session and emits Reset at sequence
1. Changed non-empty Partial text advances revision. An endpoint emits
authoritative Committed then Final with consecutive sequences and identical
identity/text. Empty final output emits nothing without a Partial, or Reset when
provisional text must be invalidated. Audio ranges derive from absolute samples.
Stage 5 exposes these events from transport and pipeline only; it does not
implement `ICaptionSource`, touch `Translator`, or change WPF startup. Stage 6
has not begun.

## Caption event lifecycle

Stage 2A defines immutable caption-event schema version 1. Each event contains:

| Field | Type | Meaning |
|---|---|---|
| `SchemaVersion` | `int` | Fixed at the currently supported value `1` |
| `SessionId` | `Guid` | Identity created by each successful source start or restart |
| `Sequence` | `long` | Monotonically increasing event order within a session |
| `SegmentId` | `long` | Ordered recognized-speech segment identity |
| `Revision` | `long` | Text revision within a segment |
| `Kind` | `CaptionEventKind` | `Partial`, `Committed`, `Final`, or `Reset` |
| `Text` | `string` | Recognized text, or empty text for `Reset` |
| `AudioStartMilliseconds` | `long?` | Optional non-negative audio start position |
| `AudioEndMilliseconds` | `long?` | Optional non-negative audio end position |
| `EmittedAtUtc` | `DateTimeOffset` | Non-default UTC event emission timestamp |

Event construction enforces these invariants:

- schema version is exactly 1;
- session identity is not empty and sequence is at least 1;
- audio positions are non-negative and end is not earlier than start;
- `Reset` uses empty text, segment 0, and revision 0;
- text events use non-whitespace text, segment at least 1, and revision at
  least 1.

The event kinds represent this lifecycle:

- **Partial** updates replace the current provisional text for a segment.
- **Committed** updates identify text stable enough to enter the translation
  pipeline.
- **Final** updates close a segment and establish its final recognized text.
- **Reset** invalidates the current provisional state after a restart, source
  change, discontinuity, or explicit reset.

`CaptionEventGate` enforces the following ordering independently of any source:

- a sequence-1 `Reset` establishes each new session;
- a newer `Reset` in the active session clears segment state;
- delayed events and resets from obsolete sessions are rejected;
- sequence values strictly increase among all events observed for the active
  session, including events rejected by later segment, revision, text, or
  lifecycle validation;
- the first segment after reset is 1, segment identities never decrease or
  skip, and a new segment starts only after the preceding segment is final;
- the first segment revision is 1, revisions never decrease, and a newer
  revision represents changed text;
- events for the same segment and revision contain identical text;
- lifecycle progression within one revision only moves forward from `Partial`
  to `Committed` to `Final`, while intermediate states may be skipped;
- no event updates a finalized segment.

The gate tracks `HighestObservedSequence` separately from
`LastAcceptedSequence`. A newer active-session sequence advances the observed
value before content validation, while only a fully accepted event advances the
accepted value and caption state. Duplicate and older checks use the observed
value. Rejected foreign-session events do not change either active-session
sequence value, and a sequence-1 `Reset` for a genuinely new session resets
both values and clears segment state. Both values are exposed as read-only
diagnostic state; the gate never mutates caption events.

## Caption source lifecycle contract

Stage 2A defines `ICaptionSource` without WPF, UI Automation, translation,
Windows Live Captions, ASR, or IPC types. A source exposes a stable identifier,
current state, latest failure reason, caption and status notifications,
cancellation-aware start and stop operations, and asynchronous disposal.

Source states are `Stopped`, `Starting`, `Running`, `Restarting`, `Unavailable`,
`Faulted`, and `Stopping`. Start must be idempotent or reject duplicates
predictably; stop is safe when already stopped; no event is emitted after stop
completes; every successful start or restart creates a new session whose first
event is `Reset` sequence 1.

## Stage 2B Windows Live Captions adapter

The production flow is:

```text
App lifecycle
→ Translator caption-source lifecycle
→ CaptionSourceHost
→ ICaptionSource
→ WindowsLiveCaptionsSource
→ ILiveCaptionsRuntime
→ LiveCaptionsRuntime
→ LiveCaptionsHandler
```

`Translator` constructs exactly one `WindowsLiveCaptionsSource` without native
side effects. `App` explicitly starts the source before starting the three
Translator loops, and cancels the loops, stops the source, and disposes its
runtime during application exit. Shutdown observes every loop and independently
attempts source stop and disposal even when loop cancellation, a managed loop,
or an earlier cleanup phase fails. Phase-specific failures are retained for
diagnostics, while `OnExit` and `ProcessExit` share the same idempotent shutdown
operation.

`CaptionSourceHost` subscribes before startup, passes every event through
`CaptionEventGate`, and stores only one lock-protected latest accepted full-
snapshot value. Source state, session generation, and the processable snapshot
are read atomically. A snapshot is processable only while the source is
`Running`; transitions to a non-running state clear it without advancing the
session generation. Delayed events still pass through the gate but cannot
restore a processable snapshot while the source is inactive. An accepted
`Reset` remains the only event that advances the processing generation, and a
new-session Reset establishes the replacement gate session. Rejected, stale,
duplicate, and foreign-session events cannot replace that value.

`LiveCaptionsRuntime` is the narrow Windows-specific boundary. It privately owns
the UI Automation window and source-owned process identity and delegates the
existing low-level launch, repair, hide, caption read, restore, and termination
operations to `LiveCaptionsHandler`. Initialization, snapshot reads, cleanup,
and native-window control use typed outcomes; failure classification never
depends on parsing diagnostic strings. Only this adapter and the low-level
handler use `AutomationElement`.

On every successful start or controlled restart,
`WindowsLiveCaptionsSource` creates a new session and first emits `Reset`
sequence 1. It polls at approximately 25 ms and emits a `Partial` event only
when the complete, non-empty raw Windows Live Captions snapshot changes. All
Partials use segment 1, with sequence and revision increasing together. The
Windows adapter deliberately does not invent `Committed` or `Final` events;
Translator retains its existing punctuation, sentence extraction, sync-count,
idle-count, and translation-queue commit heuristics.

Managed snapshot processing preserves the legacy context-clear order. When a
snapshot contains no end-of-sentence punctuation and existing contexts must be
cleared, overlay history expansion uses an effective context count of zero for
that tick; normal overlay expansion is unchanged when contexts remain valid.

Unexpected native-window loss publishes `Restarting`, waits approximately two
seconds, and performs one controlled initialization attempt. A successful
attempt starts a new session; a failed attempt becomes `Unavailable` or
`Faulted` and stops polling. Stop cancellation interrupts the restart delay and
prevents later initialization or events.

Native window show/hide support is exposed separately through the optional
`INativeCaptionWindowControl` capability. It contains no UI Automation types and
returns typed results for normal unavailability and control failures. Translator
and UI code depend only on this optional capability and no longer access
`AutomationElement` or `LiveCaptionsHandler` directly.

## Translation pipeline

Translation requests must carry the caption session and version that produced
them. When an asynchronous translation completes, the WPF application must
publish it only if that source version is still current. A late result for an
older partial or committed caption must never overwrite a newer caption or its
translation. History entries must preserve the association between source text,
caption version, and accepted translation.

## Failure isolation

Audio capture, PCM normalization, and buffering belong in the WPF process.
Failures such as a missing or disconnected audio device, unsupported endpoint
format, or buffer overrun are surfaced by the WPF application itself without
terminating the ASR worker.

Native recognition and optional CUDA execution belong in the worker so a native
crash, accelerator initialization failure, or unrecoverable model error does not
directly terminate the WPF process. The WPF application must detect worker exit
or IPC loss, expose a useful error state, clear obsolete caption state, and
support controlled restart or CPU fallback where applicable. Worker restart
creates a new caption session so delayed events and translations from the old
session can be rejected.

## Fixed constraints

- The UI application remains a .NET 8 WPF application.
- Windows 10 and Windows 11 are supported targets.
- System audio capture uses WASAPI loopback and is owned by the WPF process.
- The WPF process normalizes captured audio to 16 kHz mono PCM before sending
  it to the worker.
- Recognition runs out of process in a native worker.
- The worker receives normalized 16 kHz mono PCM via named pipe; it does not
  perform audio capture or format conversion.
- The recognition engine is `whisper.cpp`, with optional CUDA acceleration and
  CPU fallback.
- Silero VAD is part of the planned recognition pipeline and runs in the worker.
- Local IPC is versioned, uses Windows named pipes, and supports control
  messages, lifecycle messages, normalized PCM audio, worker status, errors,
  and caption events.
- Caption and translation state is versioned to prevent stale updates.
- Python is not part of the final runtime.
- The WPF process does not load the final CUDA speech-recognition runtime.
- Each implementation stage remains independently testable.

## Rejected final-runtime approaches

The following are not acceptable as the final runtime architecture:

- retaining Windows Live Captions as a mandatory caption source;
- using Python or a Python-hosted ASR service;
- loading `whisper.cpp`, CUDA, or the final speech-recognition runtime directly
  into the WPF process;
- coupling audio capture, recognition, and UI state inside one process;
- using unversioned text messages that cannot reject stale caption or
  translation results.
