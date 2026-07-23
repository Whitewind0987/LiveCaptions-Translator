# AGENTS.md

## Repository Purpose

This repository is a fork of `SakiRinn/LiveCaptions-Translator`.

The long-term goal is to support Windows 10 and Windows 11 by adding local real-time speech recognition for audio played by arbitrary applications, while preserving the existing translation, overlay, settings, and history features.

The planned recognition path is:

```text
System audio
→ [WPF] WASAPI loopback capture
→ [WPF] 16 kHz mono PCM normalization
→ [WPF] bounded audio buffering
→ [IPC] versioned named pipe
→ [ASR worker] Silero VAD
→ [ASR worker] whisper.cpp
→ [ASR worker] versioned caption events
→ [WPF] translation and overlay display
```

The implementation is intentionally divided into independently testable stages.

---

## Required Reading

Before making any change, read:

1. `docs/ARCHITECTURE.md`
2. `docs/CURRENT_STATUS.md`
3. `docs/TESTING.md`

Treat these files as the current source of truth for:

- architecture decisions;
- completed work;
- active stage;
- known issues;
- testing requirements;
- next planned task.

Do not rely on an earlier conversation, cached assumption, or previous agent report when these documents provide newer information.

---

## Scope Discipline

Only implement the explicitly requested task or stage.

Do not:

- implement later stages early;
- perform unrelated refactoring;
- clean up existing warnings unless requested;
- rename unrelated files or symbols;
- change formatting across untouched files;
- replace existing architecture decisions without approval;
- add dependencies for future work;
- modify CI, packaging, or release files unless required by the current task;
- commit or push unless explicitly requested.

When a task reveals a problem outside its scope, report it as a follow-up instead of fixing it automatically.

---

## Fixed Architecture Constraints

Unless the user explicitly approves an architecture change:

1. The existing .NET 8 WPF application remains responsible for:
   - user interface;
   - overlay captions;
   - translation providers;
   - settings;
   - translation history;
   - runtime and model management;
   - WASAPI loopback system-audio capture;
   - playback-device enumeration and selection;
   - conversion of captured audio to normalized 16 kHz mono PCM;
   - bounded audio buffering;
   - sending normalized PCM to the ASR worker through versioned local IPC;
   - supervising the ASR worker.
2. System audio will be captured through WASAPI loopback.
3. Captured audio will be normalized to 16 kHz mono PCM before recognition.
4. Final speech recognition will run in a separate native worker process.
5. The intended final ASR stack is:
   - whisper.cpp;
   - optional CUDA acceleration;
   - CPU fallback;
   - Silero VAD.
6. Communication between the WPF application and ASR worker must use a versioned local IPC protocol. The IPC must support control messages, lifecycle messages, normalized PCM audio, worker status, errors, and caption events.
7. Captions must use structured, versioned events such as:
   - `Partial`;
   - `Committed`;
   - `Final`;
   - `Reset`.
8. Translation results must be associated with caption identity and revision so stale responses cannot overwrite newer captions.
9. Python is not part of the intended final runtime.
10. The WPF process must not directly load the final CUDA speech-recognition runtime.
11. Each stage must remain independently testable.

---

## Explicitly Rejected Final Designs

Do not use the following as the final architecture:

- embedding all speech-recognition logic in a WPF window code-behind file;
- using Python as the distributed runtime;
- repeatedly writing and reading temporary WAV files for streaming recognition;
- sending every partial caption to translation APIs;
- loading the final CUDA inference runtime directly inside the WPF process;
- treating the entire current caption as one unversioned mutable string;
- assuming every audio device already provides 16 kHz mono audio;
- implementing the complete Windows 10 ASR feature in one large change.

A temporary prototype using a rejected approach requires explicit approval and must be clearly marked as disposable.

---

## Current Baseline

The baseline repository state and active stage are recorded in:

```text
docs/CURRENT_STATUS.md
```

Do not duplicate volatile branch, commit, SDK, or stage information in this file. Update `CURRENT_STATUS.md` instead.

---

## Development Workflow

For each stage:

1. Inspect the relevant code and documentation.
2. State the files expected to change.
3. Implement only the current stage.
4. Build the project.
5. Run the stage-specific tests from `docs/TESTING.md`.
6. Report failures accurately.
7. Update documentation when the stage changes project behavior or status.
8. Leave the working tree ready for human review.
9. Do not commit or push unless explicitly requested.

High-risk work should use a dedicated temporary branch and be merged into:

```text
feature/win10-asr-backend
```

only after the stage acceptance criteria pass.

---

## Build Environment

The project targets:

```text
net8.0-windows
```

Common validation commands:

```powershell
dotnet restore
dotnet build
git diff --check
git status --short
```

The focused captioning contract tests are located in:

```text
tests/LiveCaptionsTranslator.Tests/
```

Run them with:

```powershell
dotnet test tests/LiveCaptionsTranslator.Tests/LiveCaptionsTranslator.Tests.csproj
```

The Stage 3 developer audio probe is located in:

```text
tools/AudioCaptureProbe/
```

Build it with:

```powershell
dotnet build tools/AudioCaptureProbe/AudioCaptureProbe.csproj
```

Use the probe only for explicit interactive audio-capture verification. Unit
tests must use generated audio and fake endpoint/capture runtimes rather than
opening real devices. Ordinary WPF startup must not start audio capture until a
later stage provides an explicit IPC consumer and lifecycle owner.

NAudio belongs only at the narrow Windows endpoint-enumeration and WASAPI
runtime boundary. UI, buffering, normalization contracts, and tests must not
expose or depend on NAudio device objects.

Existing baseline warnings must not be presented as newly introduced warnings.

When warnings are relevant, distinguish between:

- warnings already present at the recorded baseline;
- warnings introduced by the current change.

Do not claim runtime success when only compilation was tested.

---

## Testing Rules

Testing must match the current stage.

Examples:

- an audio-capture stage should verify captured audio, not speech recognition;
- an offline-ASR stage should verify fixed audio fixtures, not claim real-time stability;
- a worker-protocol stage may use a fake worker and simulated captions;
- a translation stage must test stale-result rejection and queue behavior;
- a packaging stage must test on a clean environment.

Do not invent passing test results.

When a test cannot be run in the current environment, state:

- what was not tested;
- why it was not tested;
- what command or manual procedure remains.

Future audio fixtures and regression results belong in `docs/TESTING.md`.

---

## Source-Code Rules

Prefer:

- small classes with a single responsibility;
- explicit ownership of threads, processes, streams, and cancellation tokens;
- bounded queues and buffers;
- cancellation-aware asynchronous code;
- structured state instead of shared mutable strings;
- deterministic cleanup;
- useful error messages;
- dependency injection or narrow interfaces where they improve testability.

Avoid:

- blocking the WPF UI thread;
- unbounded queues;
- fire-and-forget tasks without error handling;
- global mutable state added for convenience;
- silent exception swallowing;
- infinite restart loops;
- hidden fallback behavior;
- mixing UI, audio capture, inference, and translation in the same class.

---

## Windows Compatibility

Windows 10 compatibility is a primary project requirement.

Do not introduce a dependency on a Windows 11-only feature unless:

- it is optional;
- it is guarded by runtime capability detection;
- Windows 10 retains a working alternative.

Windows Live Captions may remain available as a Windows 11 caption source, but application startup must not require `LiveCaptions.exe` on Windows 10.

---

## Dependency Rules

Before adding a dependency, report:

- why it is needed now;
- which stage requires it;
- supported operating systems;
- runtime and packaging impact;
- license;
- whether it adds native binaries;
- whether it changes application size materially.

Do not add dependencies for a future stage.

Pin versions deliberately. Do not perform broad dependency upgrades unless requested.

---

## Documentation Rules

Update `docs/CURRENT_STATUS.md` at the end of every completed stage.

Update `docs/TESTING.md` when:

- a new test procedure is introduced;
- a baseline result changes;
- a regression fixture is added;
- an acceptance test is completed.

Update `docs/ARCHITECTURE.md` only when an architectural decision changes or requires clarification.

Documentation must distinguish clearly between:

- implemented behavior;
- planned behavior;
- experimental behavior;
- known limitations.

---

## Git Safety

Do not:

- commit;
- push;
- force-push;
- reset;
- rebase;
- delete branches;
- discard uncommitted work;
- modify Git remotes;

unless explicitly requested.

Before finishing, run:

```powershell
git diff --check
git status --short
```

Report every modified or untracked file.

---

## Completion Report

Every implementation response must include:

1. **Scope completed**
2. **Files changed**
3. **Implementation summary**
4. **Validation performed**
5. **Validation results**
6. **Known limitations or remaining risks**
7. **Documentation updated**
8. **Confirmation that nothing was committed or pushed**, unless explicitly requested

Do not describe planned functionality as completed.

---

## Priority Order

When instructions conflict, use this order:

1. the user's current explicit request;
2. `docs/ARCHITECTURE.md`;
3. `docs/CURRENT_STATUS.md`;
4. `docs/TESTING.md`;
5. this `AGENTS.md`;
6. existing local conventions.

Stop and report the conflict when proceeding would violate a higher-priority instruction.
