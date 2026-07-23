using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.captioning;

namespace LiveCaptionsTranslator.ipc
{
    [Flags]
    public enum WorkerCapabilities : ulong
    {
        None = 0,
        ProtocolV1 = 1,
        NormalizedPcmSink = 2,
        Vad = 4,
        Whisper = 8,
        Cuda = 16,
        CaptionProduction = 32
    }

    public enum ProtocolRejectReason : ushort
    {
        ProtocolMismatch,
        AuthenticationFailed,
        SessionMismatch,
        ProcessMismatch,
        CapabilityMismatch,
        MalformedHello
    }

    public enum WorkerErrorKind : ushort
    {
        ProtocolViolation,
        InvalidStreamState,
        InvalidAudioFrame,
        ParentExited,
        InternalFailure
    }

    public sealed record WorkerHelloPayload(Guid SessionId, byte[] Nonce, int WorkerPid, ushort MinimumMinor, ushort MaximumMinor, string BuildVersion, WorkerCapabilities Capabilities);
    public sealed record HostAcceptPayload(Guid SessionId, ushort NegotiatedMinor, int SampleRate, ushort Channels, ushort BitsPerSample, ushort FrameMilliseconds, uint SamplesPerFrame, uint BytesPerFrame);
    public sealed record HostRejectPayload(ProtocolRejectReason Reason, string Diagnostic);
    public sealed record WorkerReadyPayload(Guid SessionId, int WorkerPid);
    public sealed record AudioPipeHelloPayload(Guid SessionId, byte[] Nonce, int WorkerPid);
    public sealed record AudioPipeAcceptedPayload(Guid SessionId, int WorkerPid);
    public sealed record StartAudioStreamPayload(Guid WorkerSessionId, Guid CaptureSessionId, long InitialFrameSequence, long StartedAtUnixMilliseconds, int SampleRate = NormalizedAudioFormat.SampleRate, ushort Channels = NormalizedAudioFormat.Channels, ushort BitsPerSample = NormalizedAudioFormat.BitsPerSample, ushort FrameMilliseconds = NormalizedAudioFormat.FrameDurationMilliseconds, uint SamplesPerFrame = NormalizedAudioFormat.SamplesPerFrame, uint BytesPerFrame = NormalizedAudioFormat.BytesPerFrame);
    public sealed record AudioStreamIdentityPayload(Guid WorkerSessionId, Guid CaptureSessionId);
    public sealed record AudioStreamSummaryPayload(Guid CaptureSessionId, long FramesReceived, long PcmBytesReceived, long FirstSequence, long LastSequence, long SequenceGaps, long InvalidFrames, long FirstTimestampUnixMilliseconds, long LastTimestampUnixMilliseconds);
    public sealed record HeartbeatPayload(long SentAtUnixMilliseconds);
    public sealed record WorkerStatusPayload(ushort State, string Diagnostic);
    public sealed record ErrorPayload(WorkerErrorKind Kind, string Diagnostic);
    public sealed record ShutdownPayload(Guid WorkerSessionId);

    public static class ProtocolPayloadCodec
    {
        public static byte[] Encode(WorkerHelloPayload value)
        {
            if (value.Nonce.Length != IpcProtocol.NonceBytes) throw new ArgumentException("Nonce must be 32 bytes.");
            var w = new PayloadWriter(); w.Guid(value.SessionId); w.Bytes(value.Nonce); w.Int32(value.WorkerPid); w.UInt16(value.MinimumMinor); w.UInt16(value.MaximumMinor); w.String(value.BuildVersion, 256); w.UInt64((ulong)value.Capabilities); return w.ToArray();
        }
        public static WorkerHelloPayload DecodeWorkerHello(ReadOnlySpan<byte> bytes)
        {
            var r = new PayloadReader(bytes); var v = new WorkerHelloPayload(r.Guid(), r.Bytes(IpcProtocol.NonceBytes), r.Int32(), r.UInt16(), r.UInt16(), r.String(256), (WorkerCapabilities)r.UInt64()); r.RequireEnd(); return v;
        }
        public static byte[] Encode(HostAcceptPayload v) { var w = new PayloadWriter(); w.Guid(v.SessionId); w.UInt16(v.NegotiatedMinor); w.Int32(v.SampleRate); w.UInt16(v.Channels); w.UInt16(v.BitsPerSample); w.UInt16(v.FrameMilliseconds); w.UInt32(v.SamplesPerFrame); w.UInt32(v.BytesPerFrame); return w.ToArray(); }
        public static HostAcceptPayload DecodeHostAccept(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new HostAcceptPayload(r.Guid(), r.UInt16(), r.Int32(), r.UInt16(), r.UInt16(), r.UInt16(), r.UInt32(), r.UInt32()); r.RequireEnd(); return v; }
        public static byte[] Encode(HostRejectPayload v) { var w = new PayloadWriter(); w.UInt16((ushort)v.Reason); w.String(v.Diagnostic); return w.ToArray(); }
        public static HostRejectPayload DecodeHostReject(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new HostRejectPayload((ProtocolRejectReason)r.UInt16(), r.String()); r.RequireEnd(); return v; }
        public static byte[] Encode(WorkerReadyPayload v) { var w = new PayloadWriter(); w.Guid(v.SessionId); w.Int32(v.WorkerPid); return w.ToArray(); }
        public static WorkerReadyPayload DecodeWorkerReady(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new WorkerReadyPayload(r.Guid(), r.Int32()); r.RequireEnd(); return v; }
        public static byte[] Encode(AudioPipeHelloPayload v) { if (v.Nonce.Length != IpcProtocol.NonceBytes) throw new ArgumentException("Nonce must be 32 bytes."); var w = new PayloadWriter(); w.Guid(v.SessionId); w.Bytes(v.Nonce); w.Int32(v.WorkerPid); return w.ToArray(); }
        public static AudioPipeHelloPayload DecodeAudioPipeHello(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new AudioPipeHelloPayload(r.Guid(), r.Bytes(IpcProtocol.NonceBytes), r.Int32()); r.RequireEnd(); return v; }
        public static byte[] Encode(AudioPipeAcceptedPayload v) { var w = new PayloadWriter(); w.Guid(v.SessionId); w.Int32(v.WorkerPid); return w.ToArray(); }
        public static AudioPipeAcceptedPayload DecodeAudioPipeAccepted(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new AudioPipeAcceptedPayload(r.Guid(), r.Int32()); r.RequireEnd(); return v; }
        public static byte[] Encode(StartAudioStreamPayload v) { var w = new PayloadWriter(); w.Guid(v.WorkerSessionId); w.Guid(v.CaptureSessionId); w.Int64(v.InitialFrameSequence); w.Int64(v.StartedAtUnixMilliseconds); w.Int32(v.SampleRate); w.UInt16(v.Channels); w.UInt16(v.BitsPerSample); w.UInt16(v.FrameMilliseconds); w.UInt32(v.SamplesPerFrame); w.UInt32(v.BytesPerFrame); return w.ToArray(); }
        public static StartAudioStreamPayload DecodeStartAudioStream(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new StartAudioStreamPayload(r.Guid(), r.Guid(), r.Int64(), r.Int64(), r.Int32(), r.UInt16(), r.UInt16(), r.UInt16(), r.UInt32(), r.UInt32()); r.RequireEnd(); return v; }
        public static byte[] Encode(AudioStreamIdentityPayload v) { var w = new PayloadWriter(); w.Guid(v.WorkerSessionId); w.Guid(v.CaptureSessionId); return w.ToArray(); }
        public static AudioStreamIdentityPayload DecodeAudioStreamIdentity(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new AudioStreamIdentityPayload(r.Guid(), r.Guid()); r.RequireEnd(); return v; }
        public static byte[] Encode(AudioStreamSummaryPayload v) { var w = new PayloadWriter(); w.Guid(v.CaptureSessionId); w.Int64(v.FramesReceived); w.Int64(v.PcmBytesReceived); w.Int64(v.FirstSequence); w.Int64(v.LastSequence); w.Int64(v.SequenceGaps); w.Int64(v.InvalidFrames); w.Int64(v.FirstTimestampUnixMilliseconds); w.Int64(v.LastTimestampUnixMilliseconds); return w.ToArray(); }
        public static AudioStreamSummaryPayload DecodeAudioStreamSummary(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new AudioStreamSummaryPayload(r.Guid(), r.Int64(), r.Int64(), r.Int64(), r.Int64(), r.Int64(), r.Int64(), r.Int64(), r.Int64()); r.RequireEnd(); return v; }
        public static byte[] Encode(HeartbeatPayload v) { var w = new PayloadWriter(); w.Int64(v.SentAtUnixMilliseconds); return w.ToArray(); }
        public static HeartbeatPayload DecodeHeartbeat(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new HeartbeatPayload(r.Int64()); r.RequireEnd(); return v; }
        public static byte[] Encode(WorkerStatusPayload v) { var w = new PayloadWriter(); w.UInt16(v.State); w.String(v.Diagnostic); return w.ToArray(); }
        public static WorkerStatusPayload DecodeWorkerStatus(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new WorkerStatusPayload(r.UInt16(), r.String()); r.RequireEnd(); return v; }
        public static byte[] Encode(ErrorPayload v) { var w = new PayloadWriter(); w.UInt16((ushort)v.Kind); w.String(v.Diagnostic); return w.ToArray(); }
        public static ErrorPayload DecodeError(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new ErrorPayload((WorkerErrorKind)r.UInt16(), r.String()); r.RequireEnd(); return v; }
        public static byte[] Encode(ShutdownPayload v) { var w = new PayloadWriter(); w.Guid(v.WorkerSessionId); return w.ToArray(); }
        public static ShutdownPayload DecodeShutdown(ReadOnlySpan<byte> b) { var r = new PayloadReader(b); var v = new ShutdownPayload(r.Guid()); r.RequireEnd(); return v; }

        public static byte[] EncodeAudioFrame(Guid workerSessionId, NormalizedAudioFrame frame)
        {
            var w = new PayloadWriter(); w.Guid(workerSessionId); w.Guid(frame.SessionId); w.Int64(frame.Sequence); w.Int64(frame.StartSampleIndex); w.Int64(frame.CapturedAtUtc.ToUnixTimeMilliseconds()); w.UInt32((uint)frame.Payload.Length); w.Bytes(frame.Payload.AsSpan()); var bytes = w.ToArray();
            if (bytes.Length != IpcProtocol.AudioFramePayloadSize) throw new InvalidOperationException("Audio frame wire size is invalid."); return bytes;
        }

        public static (Guid WorkerSessionId, NormalizedAudioFrame Frame) DecodeAudioFrame(ReadOnlySpan<byte> b)
        {
            var r = new PayloadReader(b); var worker = r.Guid(); var capture = r.Guid(); var sequence = r.Int64(); var index = r.Int64(); var timestamp = r.Int64(); var length = r.UInt32();
            if (length != NormalizedAudioFormat.BytesPerFrame) throw new IpcProtocolException(ProtocolFailureKind.InvalidPayload, "Audio PCM length is invalid.");
            var frame = new NormalizedAudioFrame(capture, sequence, index, DecodeTimestamp(timestamp), r.Bytes((int)length)); r.RequireEnd(); return (worker, frame);
        }

        public static byte[] EncodeCaptionEvent(CaptionEvent v)
        {
            var w = new PayloadWriter(); w.Int32(v.SchemaVersion); w.Guid(v.SessionId); w.Int64(v.Sequence); w.Int64(v.SegmentId); w.Int64(v.Revision); w.UInt16((ushort)v.Kind); w.String(v.Text, 32 * 1024); w.Byte(v.AudioStartMilliseconds.HasValue ? (byte)1 : (byte)0); if (v.AudioStartMilliseconds.HasValue) w.Int64(v.AudioStartMilliseconds.Value); w.Byte(v.AudioEndMilliseconds.HasValue ? (byte)1 : (byte)0); if (v.AudioEndMilliseconds.HasValue) w.Int64(v.AudioEndMilliseconds.Value); w.Int64(v.EmittedAtUtc.ToUnixTimeMilliseconds()); return w.ToArray();
        }

        public static CaptionEvent DecodeCaptionEvent(ReadOnlySpan<byte> b)
        {
            var r = new PayloadReader(b); var schema = r.Int32(); var session = r.Guid(); var sequence = r.Int64(); var segment = r.Int64(); var revision = r.Int64(); var kind = (CaptionEventKind)r.UInt16(); var text = r.String(32 * 1024); var hasStart = r.Byte(); var start = hasStart == 1 ? r.Int64() : (long?)null; var hasEnd = r.Byte(); var end = hasEnd == 1 ? r.Int64() : (long?)null; var emitted = r.Int64(); r.RequireEnd();
            if (hasStart > 1 || hasEnd > 1) throw new IpcProtocolException(ProtocolFailureKind.InvalidPayload, "Caption optional marker is invalid.");
            return new CaptionEvent(schema, session, sequence, segment, revision, kind, text, start, end, DecodeTimestamp(emitted));
        }

        private static DateTimeOffset DecodeTimestamp(long unixMilliseconds)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds); }
            catch (ArgumentOutOfRangeException ex) { throw new IpcProtocolException(ProtocolFailureKind.InvalidPayload, $"Timestamp is outside the supported range: {ex.Message}"); }
        }
    }
}
