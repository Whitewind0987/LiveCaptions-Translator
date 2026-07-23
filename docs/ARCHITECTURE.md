# Planned Architecture

## Status

This document records the planned architecture for the Windows 10/11-compatible
fork. Stage 2A implements the source-independent caption contracts and ordering
core. Stage 2B implements the Windows Live Captions source adapter and integrates
those contracts into the production runtime.

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
