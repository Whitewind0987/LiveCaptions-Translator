using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace LiveCaptionsTranslator.ipc
{
    public static class IpcProtocol
    {
        public const uint Magic = 0x3150434c;
        public const ushort Major = 1;
        public const ushort Minor = 0;
        public const int EnvelopeSize = 40;
        public const int MaximumControlPayload = 64 * 1024;
        public const int MaximumStringBytes = 4096;
        public const int NonceBytes = 32;
        public const string NonceEnvironmentVariable = "LIVE_CAPTIONS_ASR_NONCE";
        public const int AudioFramePayloadSize = 700;
    }

    [Flags]
    public enum IpcMessageFlags : ushort
    {
        None = 0,
        Optional = 1
    }

    public enum IpcMessageType : ushort
    {
        WorkerHello = 1,
        HostAccept = 2,
        HostReject = 3,
        WorkerReady = 4,
        StartAudioStream = 5,
        AudioStreamStarted = 6,
        StopAudioStream = 7,
        AudioStreamStopped = 8,
        Ping = 9,
        Pong = 10,
        WorkerStatus = 11,
        AudioProgress = 12,
        Error = 13,
        Shutdown = 14,
        ShutdownAcknowledged = 15,
        CaptionEvent = 16,
        AudioPipeHello = 17,
        AudioPipeAccepted = 18,
        AudioFrame = 100
    }

    public enum ProtocolFailureKind
    {
        InvalidMagic,
        UnsupportedMajor,
        UnsupportedMinor,
        UnknownRequiredMessage,
        OversizedPayload,
        TruncatedMessage,
        InvalidPayload,
        InvalidUtf8,
        OutOfOrder
    }

    public sealed class IpcProtocolException : Exception
    {
        public IpcProtocolException(ProtocolFailureKind kind, string message) : base(message) =>
            Kind = kind;

        public ProtocolFailureKind Kind { get; }
    }

    public readonly record struct IpcEnvelope(
        ushort Major,
        ushort Minor,
        ushort RawMessageType,
        IpcMessageFlags Flags,
        uint PayloadLength,
        ulong Sequence,
        Guid CorrelationId)
    {
        public bool IsKnownMessageType => Enum.IsDefined(typeof(IpcMessageType), RawMessageType);
        public IpcMessageType MessageType => (IpcMessageType)RawMessageType;

        public byte[] Encode()
        {
            var bytes = new byte[IpcProtocol.EnvelopeSize];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, IpcProtocol.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), Major);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), Minor);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), RawMessageType);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), PayloadLength);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), Sequence);
            GuidWire.Write(CorrelationId, bytes.AsSpan(24));
            return bytes;
        }

        public static IpcEnvelope Decode(ReadOnlySpan<byte> bytes, int maximumPayload)
        {
            if (bytes.Length < IpcProtocol.EnvelopeSize)
                throw new IpcProtocolException(ProtocolFailureKind.TruncatedMessage, "The IPC envelope is truncated.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != IpcProtocol.Magic)
                throw new IpcProtocolException(ProtocolFailureKind.InvalidMagic, "The IPC magic is invalid.");
            var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
            if (major != IpcProtocol.Major)
                throw new IpcProtocolException(ProtocolFailureKind.UnsupportedMajor, $"Protocol major {major} is unsupported.");
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
            if (payloadLength > maximumPayload)
                throw new IpcProtocolException(ProtocolFailureKind.OversizedPayload, $"Payload length {payloadLength} exceeds {maximumPayload}.");
            var envelope = new IpcEnvelope(
                major,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]),
                (IpcMessageFlags)BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]),
                payloadLength,
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
                GuidWire.Read(bytes[24..40]));
            if (!envelope.IsKnownMessageType && !envelope.Flags.HasFlag(IpcMessageFlags.Optional))
                throw new IpcProtocolException(ProtocolFailureKind.UnknownRequiredMessage, $"Required message type {envelope.RawMessageType} is unknown.");
            return envelope;
        }
    }

    public sealed record IpcMessage(IpcEnvelope Envelope, byte[] Payload);

    public static class GuidWire
    {
        public static void Write(Guid value, Span<byte> destination)
        {
            if (destination.Length < 16) throw new ArgumentException("Guid destination is too short.", nameof(destination));
            Span<byte> dotnet = stackalloc byte[16];
            value.TryWriteBytes(dotnet);
            destination[0] = dotnet[3]; destination[1] = dotnet[2]; destination[2] = dotnet[1]; destination[3] = dotnet[0];
            destination[4] = dotnet[5]; destination[5] = dotnet[4];
            destination[6] = dotnet[7]; destination[7] = dotnet[6];
            dotnet[8..].CopyTo(destination[8..]);
        }

        public static Guid Read(ReadOnlySpan<byte> source)
        {
            if (source.Length < 16) throw new IpcProtocolException(ProtocolFailureKind.TruncatedMessage, "Guid is truncated.");
            Span<byte> dotnet = stackalloc byte[16];
            dotnet[0] = source[3]; dotnet[1] = source[2]; dotnet[2] = source[1]; dotnet[3] = source[0];
            dotnet[4] = source[5]; dotnet[5] = source[4];
            dotnet[6] = source[7]; dotnet[7] = source[6];
            source[8..16].CopyTo(dotnet[8..]);
            return new Guid(dotnet);
        }
    }

    internal sealed class PayloadWriter
    {
        private readonly MemoryStream stream = new();
        public void Byte(byte value) => stream.WriteByte(value);
        public void UInt16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); stream.Write(b); }
        public void UInt32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); stream.Write(b); }
        public void Int32(int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, value); stream.Write(b); }
        public void UInt64(ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); stream.Write(b); }
        public void Int64(long value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, value); stream.Write(b); }
        public void Guid(Guid value) { Span<byte> b = stackalloc byte[16]; GuidWire.Write(value, b); stream.Write(b); }
        public void Bytes(ReadOnlySpan<byte> value) => stream.Write(value);
        public void String(string value, int maximumBytes = IpcProtocol.MaximumStringBytes)
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(value ?? throw new ArgumentNullException(nameof(value)));
            if (bytes.Length > maximumBytes) throw new ArgumentOutOfRangeException(nameof(value));
            UInt32((uint)bytes.Length); Bytes(bytes);
        }
        public byte[] ToArray() => stream.ToArray();
    }

    internal ref struct PayloadReader
    {
        private ReadOnlySpan<byte> bytes;
        private int offset;
        public PayloadReader(ReadOnlySpan<byte> bytes) { this.bytes = bytes; offset = 0; }
        public int Remaining => bytes.Length - offset;
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Remaining) throw new IpcProtocolException(ProtocolFailureKind.TruncatedMessage, "The IPC payload is truncated.");
            var result = bytes.Slice(offset, count); offset += count; return result;
        }
        public byte Byte() => Take(1)[0];
        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public Guid Guid() => GuidWire.Read(Take(16));
        public byte[] Bytes(int count) => Take(count).ToArray();
        public string String(int maximumBytes = IpcProtocol.MaximumStringBytes)
        {
            var length = UInt32();
            if (length > maximumBytes) throw new IpcProtocolException(ProtocolFailureKind.OversizedPayload, "String exceeds its bound.");
            try { return new UTF8Encoding(false, true).GetString(Take(checked((int)length))); }
            catch (DecoderFallbackException ex) { throw new IpcProtocolException(ProtocolFailureKind.InvalidUtf8, ex.Message); }
        }
        public void RequireEnd()
        {
            if (Remaining != 0) throw new IpcProtocolException(ProtocolFailureKind.InvalidPayload, "IPC payload has trailing bytes.");
        }
    }
}
