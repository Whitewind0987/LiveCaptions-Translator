# Planned Architecture

## Status

This document records the planned architecture for the Windows 10/11-compatible
fork. Stage 0 is documentation and baseline verification only. None of the
architecture described below is implemented by Stage 0.

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

The worker will publish caption events with a stream/session identity and a
monotonically increasing version or sequence value:

- **Partial** updates replace the current provisional text for a segment.
- **Committed** updates identify text stable enough to enter the translation
  pipeline.
- **Final** updates close a segment and establish its final recognized text.
- **Reset** invalidates the current provisional state after a restart, source
  change, discontinuity, or explicit reset.

Consumers must apply events in order and ignore events from an obsolete session
or older version. The exact schema and commit policy belong to later stages.

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
