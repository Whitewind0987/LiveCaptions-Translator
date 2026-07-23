# IPC Protocol v1.0

## Goals and Stage 4 boundary

Protocol 1.0 is the local, binary contract between the WPF host and the
separately built `LiveCaptionsAsrWorker.exe`. Stage 4 transports normalized
audio and lifecycle diagnostics only. The worker validates, counts, and
discards PCM. It does not load VAD, Whisper, CUDA, or models and does not emit
captions during normal operation. `CaptionEvent` exists solely to establish the
future wire contract.

The protocol is little-endian except for Guid fields, which use the 16 RFC 4122
bytes in textual/network order. No native structure or .NET-specific Guid byte
layout is written directly.

## Two-pipe topology and ownership

- The host creates both byte-mode named-pipe servers before process launch.
- The control pipe is full duplex. Each side has one reader and serialized
  writes. It carries handshake, lifecycle, heartbeat, progress, errors, and
  future caption events.
- The audio pipe is temporarily full duplex for its authenticated binding.
  After `AudioPipeHello`/`AudioPipeAccepted`, one host writer sends sequential
  normalized frames and one worker reader validates them; no PCM is sent before
  that acknowledgment.
- Both pipe names are independent, cryptographically random, local names and
  allow one current-user client.
- A fresh worker session creates fresh pipe names, session Guid, and 32-byte
  nonce. Old-session traffic is rejected.

## Security model

The host passes only control pipe name, audio pipe name, worker-session Guid,
and parent PID in `ProcessStartInfo.ArgumentList`. The random nonce is passed in
the private inherited `LIVE_CAPTIONS_ASR_NONCE` environment value and is never
logged. `WorkerHello` authenticates the control connection and an independent
`AudioPipeHello` proves the same session, nonce, and exact owned PID on the
audio connection. The host sends neither audio nor the nonce to an
unauthenticated audio client. The host does not search PATH or kill by process
name.

The owned worker is assigned to a Windows Job Object with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. Independently, the worker monitors the
supplied parent PID and closes its pipe operations if the parent exits.

## Version negotiation and compatibility

The current version is major 1, minor 0.

- A different major version is fatal.
- Any envelope minor other than the negotiated/current minor is fatal.
- `WorkerHello` advertises an inclusive supported minor range; the host selects
  the highest mutually supported minor in `HostAccept`.
- An unknown required message type is fatal.
- An unknown message may be ignored only when envelope flag bit 0 (`Optional`)
  is set.
- `Optional` is valid only for an unknown extension message. Unknown flag bits,
  or `Optional` on a known message, are fatal.
- Malformed, truncated, oversized, out-of-order, or invalid UTF-8 data is a
  typed protocol violation and closes the session.

## Common envelope

Every control and audio message starts with this fixed 40-byte envelope:

| Offset | Size | Field | Encoding |
|---:|---:|---|---|
| 0 | 4 | Magic | `4C 43 50 31` (`LCP1`) |
| 4 | 2 | Protocol major | unsigned little-endian |
| 6 | 2 | Protocol minor | unsigned little-endian |
| 8 | 2 | Message type | unsigned little-endian |
| 10 | 2 | Flags | bit 0 is `Optional` |
| 12 | 4 | Payload length | unsigned little-endian |
| 16 | 8 | Message sequence | unsigned little-endian, starts at 1 |
| 24 | 16 | Correlation ID | RFC 4122/network-order Guid bytes |

Sequences strictly increase independently in each pipe direction. Request and
response messages share a non-empty correlation ID. Unsolicited progress and
error messages may use an empty correlation ID.

Control payloads are at most 65,536 bytes. The audio pipe carries exact
700-byte frames and the bounded 72-byte `AudioStreamEnd` payload. Reads and
writes are exact loops; pipe read boundaries have no application-level meaning.

## Primitive encoding

- Unsigned and signed integers are fixed-width little-endian two's-complement.
- Boolean/optional markers are one byte and accept only 0 or 1.
- A Guid is 16 RFC 4122/network-order bytes.
- A string is a 4-byte unsigned byte length followed by strict UTF-8 without a
  terminator. General diagnostics are limited to 4,096 bytes, build strings to
  256 bytes, and caption text to 32 KiB.
- Timestamps are signed 64-bit Unix milliseconds in UTC and must map to a valid
  non-default timestamp.

## Message types and payloads

| ID | Name | Payload fields in order |
|---:|---|---|
| 1 | `WorkerHello` | worker session Guid; nonce[32]; worker PID i32; minimum minor u16; maximum minor u16; build UTF-8; capabilities u64 |
| 2 | `HostAccept` | worker session Guid; negotiated minor u16; sample rate i32; channels u16; bits/sample u16; frame ms u16; samples/frame u32; bytes/frame u32 |
| 3 | `HostReject` | typed reason u16; diagnostic UTF-8 |
| 4 | `WorkerReady` | worker session Guid; worker PID i32 |
| 5 | `StartAudioStream` | worker session Guid; capture session Guid; initial sequence i64; start Unix ms i64; fixed format fields from `HostAccept` |
| 6 | `AudioStreamStarted` | worker session Guid; capture session Guid |
| 7 | `StopAudioStream` | worker session Guid; capture session Guid |
| 8 | `AudioStreamStopped` | audio summary below |
| 9 | `Ping` | sent Unix ms i64 |
| 10 | `Pong` | matching Ping payload and correlation |
| 11 | `WorkerStatus` | typed state u16; diagnostic UTF-8 |
| 12 | `AudioProgress` | audio summary below |
| 13 | `Error` | typed worker error u16; diagnostic UTF-8 |
| 14 | `Shutdown` | worker session Guid |
| 15 | `ShutdownAcknowledged` | worker session Guid |
| 16 | `CaptionEvent` | caption payload below |
| 17 | `AudioPipeHello` | worker session Guid; nonce[32]; worker PID i32 |
| 18 | `AudioPipeAccepted` | worker session Guid; worker PID i32 |
| 19 | `AudioStreamEnd` | worker session Guid; capture session Guid; frames sent i64; PCM bytes sent i64; first sequence i64; final sequence i64; source sequence gaps i64 |
| 100 | `AudioFrame` | exact audio payload below |

The audio summary is: capture session Guid; frames received i64; PCM bytes
received i64; first sequence i64; last sequence i64; sequence gaps i64; invalid
frames i64; first timestamp i64; last timestamp i64. Progress is bounded to
once per 50 accepted frames. The final summary is sent after stream stop.

Capability bits are: bit 0 protocol v1, bit 1 normalized PCM sink, bit 2 VAD,
bit 3 Whisper, bit 4 CUDA, and bit 5 caption production. The Stage 4 worker sets
only bits 0 and 1.

## CaptionEvent payload

The payload maps without semantic changes to the Stage 2 model:

1. schema version i32;
2. caption session Guid;
3. sequence i64;
4. segment ID i64;
5. revision i64;
6. kind u16 (`Partial=0`, `Committed=1`, `Final=2`, `Reset=3`);
7. bounded caption UTF-8;
8. audio-start-present byte and optional audio-start milliseconds i64;
9. audio-end-present byte and optional audio-end milliseconds i64;
10. emitted-at Unix milliseconds i64.

The C# codec constructs the existing validated `CaptionEvent`; it is not wired
to `CaptionSourceHost` or `Translator` in Stage 4.

## Normalized audio frame

`StartAudioStream` and every frame require 16,000 Hz, mono, signed PCM16
little-endian, 20 ms, 320 samples, and 640 PCM bytes. The exact 700-byte audio
payload is:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 16 | worker-session Guid |
| 16 | 16 | capture-session Guid |
| 32 | 8 | frame sequence i64 |
| 40 | 8 | starting sample index i64 |
| 48 | 8 | capture Unix milliseconds i64 |
| 56 | 4 | PCM byte length u32, exactly 640 |
| 60 | 640 | immutable PCM bytes |

The worker requires the active identities and lifecycle, strictly increasing
sequences and sample-index deltas of `sequence delta × 320`, nondecreasing
timestamps, and exact payload length. A rejected candidate does not mutate
accepted sequence, gap, byte, frame, sample-index, or timestamp state. It
retains bounded statistics only;
it does not retain unbounded audio or write WAV files.

The first accepted frame may be newer than the initial sequence declared by
`StartAudioStream`. Its sample index must be
`(frame sequence - initial sequence) × 320`; that initial gap is included in
the source/worker gap totals. This permits recovery after frames were dropped
before the pump's first successful write without weakening sample continuity.

## State machines and sequences

Handshake sequence:

```text
connect both pipes
→ WorkerHello + AudioPipeHello
→ AudioPipeAccepted
→ HostAccept | HostReject
→ WorkerReady
```

Streaming sequence:

```text
StartAudioStream
→ AudioStreamStarted
→ AudioFrame* (+ bounded AudioProgress)
→ AudioStreamEnd (audio pipe)
→ StopAudioStream
→ AudioStreamStopped
```

`AudioStreamEnd` is the explicit audio-pipe drain barrier; pipe closure is not
an end marker. The host stops capture, drains and joins the pump, sends one end
message with the pump totals, then sends control `StopAudioStream`. The worker
waits up to two seconds for the matching end message even when control Stop is
observed first. End before start, duplicate end, wrong identity/totals/final
sequence, frames after end, and a missing/timed-out end are protocol failures.

Shutdown sequence:

```text
stop capture → drain bounded Stage 3 buffer → AudioStreamEnd → stop stream
→ stop heartbeat → Shutdown → ShutdownAcknowledged → worker exit
```

After `WorkerReady`, the host sends one Ping approximately every two seconds.
Each Pong must use the matching correlation ID. Approximately six seconds
without a valid Pong becomes typed `HeartbeatTimeout`. Heartbeat and all pipe
tasks are cancelled and observed during cleanup.

## Failure behavior and limits

Handshake/connect/readiness defaults to a five-second bound. Graceful shutdown
is bounded; on timeout, only the owned process tree is terminated. Process exit,
pipe loss, protocol rejection, heartbeat timeout, capture failure, audio-pump
failure, forced-termination failure, and cleanup failure have typed managed
failure kinds. Human-readable text supplements but never determines the kind.
An unsolicited worker `Error` becomes `WorkerReportedError`; it is not folded
into a generic protocol or pipe-closure result.
The transport exposes one stored typed terminal completion. A monitor only
records/signals the first terminal request and returns. A separately owned,
tracked coordinator begins cleanup after that origin can exit and can therefore
join every heartbeat/process/transport monitor without self-await. Stop,
restart, disposal, and the pipeline join that exact coordinator/cleanup task.
Original failure type and every later cleanup failure remain separately
diagnosable.

The Stage 3 250-frame drop-oldest buffer remains the only PCM backpressure
queue. The Stage 4 pump reads it directly and performs one sequential pipe
write per frame. It creates no second audio queue and no task per frame.

## Golden vectors

Shared deterministic payload vectors are in
`protocol/v1/test-vectors/protocol-v1.hex`. Both C# and C++ tests consume the
same source-controlled file to decode every field, encode the expected value,
and compare every byte for all 13 vectors, including both audio-binding
messages. This locks field order, integer width, Guid order, timestamps, UTF-8,
caption encoding, `AudioStreamEnd`, and the exact 700-byte audio frame.

## Stage 4 limitations

Stage 4 is developer-probe and future-coordinator infrastructure. Ordinary WPF
startup does not construct the supervisor, start capture, or launch the worker.
No packaging resolver is integrated. No VAD, Whisper, CUDA, model handling,
recognition, production captions, or translation integration exists; those
remain explicitly approved future stages.
