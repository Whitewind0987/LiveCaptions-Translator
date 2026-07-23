using System.Buffers.Binary;
using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.captioning;
using LiveCaptionsTranslator.ipc;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class IpcProtocolTests
{
    private static readonly Guid Worker = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Capture = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void EnvelopeRoundTripsWithRfcGuidOrder()
    {
        var value = new IpcEnvelope(1, 0, (ushort)IpcMessageType.WorkerHello, IpcMessageFlags.None, 12, 9, Worker);
        var bytes = value.Encode();
        Assert.Equal(IpcProtocol.EnvelopeSize, bytes.Length);
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), bytes[24..40]);
        Assert.Equal(value, IpcEnvelope.Decode(bytes, IpcProtocol.MaximumControlPayload));
    }

    [Theory]
    [InlineData(0, ProtocolFailureKind.InvalidMagic)]
    [InlineData(1, ProtocolFailureKind.UnsupportedMajor)]
    [InlineData(2, ProtocolFailureKind.OversizedPayload)]
    [InlineData(3, ProtocolFailureKind.UnknownRequiredMessage)]
    public void InvalidEnvelopesAreTyped(int scenario, ProtocolFailureKind expected)
    {
        var bytes = new IpcEnvelope(1, 0, 1, 0, 0, 1, Guid.Empty).Encode();
        if (scenario == 0) bytes[0] = 0;
        if (scenario == 1) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 2);
        if (scenario == 2) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), IpcProtocol.MaximumControlPayload + 1);
        if (scenario == 3) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 999);
        var ex = Assert.Throws<IpcProtocolException>(() => IpcEnvelope.Decode(bytes, IpcProtocol.MaximumControlPayload));
        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public void OptionalUnknownEnvelopeCanBeDecoded()
    {
        var bytes = new IpcEnvelope(1, 0, 999, IpcMessageFlags.Optional, 0, 1, Guid.Empty).Encode();
        Assert.False(IpcEnvelope.Decode(bytes, 1).IsKnownMessageType);
    }

    [Fact]
    public async Task FragmentedReadsAreReassembled()
    {
        var payload = ProtocolPayloadCodec.Encode(new WorkerReadyPayload(Worker, 1234));
        var header = new IpcEnvelope(1, 0, 4, 0, (uint)payload.Length, 1, Guid.Empty).Encode();
        await using var stream = new IpcMessageStream(new FragmentedReadStream(header.Concat(payload).ToArray()));
        var message = await stream.ReadAsync(Token);
        Assert.Equal(new WorkerReadyPayload(Worker, 1234), ProtocolPayloadCodec.DecodeWorkerReady(message!.Payload));
    }

    [Fact]
    public async Task TruncatedPayloadIsRejected()
    {
        var header = new IpcEnvelope(1, 0, 4, 0, 20, 1, Guid.Empty).Encode();
        await using var stream = new IpcMessageStream(new MemoryStream(header.Concat(new byte[2]).ToArray()));
        var ex = await Assert.ThrowsAsync<IpcProtocolException>(() => stream.ReadAsync(Token));
        Assert.Equal(ProtocolFailureKind.TruncatedMessage, ex.Kind);
    }

    [Fact]
    public void CaptionEventRoundTrips()
    {
        var value = new CaptionEvent(1, Capture, 2, 1, 1, CaptionEventKind.Partial, "hello", 0, 20, DateTimeOffset.FromUnixTimeMilliseconds(1700000001000));
        Assert.Equal(value, ProtocolPayloadCodec.DecodeCaptionEvent(ProtocolPayloadCodec.EncodeCaptionEvent(value)));
    }

    [Fact]
    public void AudioFrameHasExactWireSizeAndRoundTrips()
    {
        var frame = new NormalizedAudioFrame(Capture, 1, 0, DateTimeOffset.FromUnixTimeMilliseconds(1700000000020), new byte[640]);
        var bytes = ProtocolPayloadCodec.EncodeAudioFrame(Worker, frame);
        Assert.Equal(IpcProtocol.AudioFramePayloadSize, bytes.Length);
        var decoded = ProtocolPayloadCodec.DecodeAudioFrame(bytes);
        Assert.Equal(Worker, decoded.WorkerSessionId);
        Assert.Equal(frame.Payload, decoded.Frame.Payload);
    }

    [Fact]
    public void InvalidAudioLengthIsRejected()
    {
        var frame = new NormalizedAudioFrame(Capture, 1, 0, DateTimeOffset.UtcNow, new byte[640]);
        var bytes = ProtocolPayloadCodec.EncodeAudioFrame(Worker, frame);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), 639);
        Assert.Throws<IpcProtocolException>(() => ProtocolPayloadCodec.DecodeAudioFrame(bytes));
    }

    [Fact]
    public void OutOfRangeTimestampIsTypedProtocolFailure()
    {
        var frame = new NormalizedAudioFrame(Capture, 1, 0, DateTimeOffset.UtcNow, new byte[640]);
        var bytes = ProtocolPayloadCodec.EncodeAudioFrame(Worker, frame);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), long.MaxValue);
        var ex = Assert.Throws<IpcProtocolException>(() => ProtocolPayloadCodec.DecodeAudioFrame(bytes));
        Assert.Equal(ProtocolFailureKind.InvalidPayload, ex.Kind);
    }

    [Fact]
    public void InvalidUtf8IsRejected()
    {
        var bytes = new byte[] { 0, 0, 1, 0, 0, 0, 0xff };
        var ex = Assert.Throws<IpcProtocolException>(() => ProtocolPayloadCodec.DecodeError(bytes));
        Assert.Equal(ProtocolFailureKind.InvalidUtf8, ex.Kind);
    }

    [Fact]
    public async Task LargeWritesUseBoundedExactChunks()
    {
        await using var stream = new RecordingWriteStream();
        await PipeIo.WriteExactlyAsync(stream, new byte[40_000], Token);
        Assert.Equal(40_000, stream.Length);
        Assert.True(stream.MaximumWrite <= 16 * 1024);
    }

    [Fact]
    public void SharedGoldenVectorsEncodeExactly()
    {
        var vectors = ReadVectors();
        var nonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.Equal(vectors["WorkerHello"], ProtocolPayloadCodec.Encode(new WorkerHelloPayload(Worker, nonce, 1234, 0, 0, "stage4-vector", (WorkerCapabilities)3)));
        Assert.Equal(vectors["HostAccept"], ProtocolPayloadCodec.Encode(new HostAcceptPayload(Worker, 0, 16000, 1, 16, 20, 320, 640)));
        Assert.Equal(vectors["WorkerReady"], ProtocolPayloadCodec.Encode(new WorkerReadyPayload(Worker, 1234)));
        Assert.Equal(vectors["StartAudioStream"], ProtocolPayloadCodec.Encode(new StartAudioStreamPayload(Worker, Capture, 1, 1700000000000)));
        var frame = new NormalizedAudioFrame(Capture, 1, 0, DateTimeOffset.FromUnixTimeMilliseconds(1700000000020), new byte[640]);
        Assert.Equal(vectors["AudioFrame"], ProtocolPayloadCodec.EncodeAudioFrame(Worker, frame));
        Assert.Equal(vectors["AudioProgress"], ProtocolPayloadCodec.Encode(new AudioStreamSummaryPayload(Capture, 50, 32000, 1, 50, 0, 0, 1700000000020, 1700000001000)));
        Assert.Equal(vectors["Error"], ProtocolPayloadCodec.Encode(new ErrorPayload((WorkerErrorKind)2, "vector error")));
        var caption = new CaptionEvent(1, Capture, 2, 1, 1, CaptionEventKind.Partial, "hello", 0, 20, DateTimeOffset.FromUnixTimeMilliseconds(1700000001000));
        Assert.Equal(vectors["CaptionEvent"], ProtocolPayloadCodec.EncodeCaptionEvent(caption));
        Assert.Equal(vectors["Shutdown"], ProtocolPayloadCodec.Encode(new ShutdownPayload(Worker)));
        Assert.Equal(vectors["ShutdownAcknowledged"], ProtocolPayloadCodec.Encode(new ShutdownPayload(Worker)));
    }

    [Fact]
    public void SharedGoldenVectorsDecodeExpectedFields()
    {
        var v = ReadVectors();
        Assert.Equal(Worker, ProtocolPayloadCodec.DecodeWorkerHello(v["WorkerHello"]).SessionId);
        Assert.Equal(16000, ProtocolPayloadCodec.DecodeHostAccept(v["HostAccept"]).SampleRate);
        Assert.Equal(1234, ProtocolPayloadCodec.DecodeWorkerReady(v["WorkerReady"]).WorkerPid);
        Assert.Equal(Capture, ProtocolPayloadCodec.DecodeStartAudioStream(v["StartAudioStream"]).CaptureSessionId);
        Assert.Equal(1, ProtocolPayloadCodec.DecodeAudioFrame(v["AudioFrame"]).Frame.Sequence);
        Assert.Equal(50, ProtocolPayloadCodec.DecodeAudioStreamSummary(v["AudioProgress"]).FramesReceived);
        Assert.Equal("vector error", ProtocolPayloadCodec.DecodeError(v["Error"]).Diagnostic);
        Assert.Equal("hello", ProtocolPayloadCodec.DecodeCaptionEvent(v["CaptionEvent"]).Text);
        Assert.Equal(Worker, ProtocolPayloadCodec.DecodeShutdown(v["Shutdown"]).WorkerSessionId);
    }

    private static Dictionary<string, byte[]> ReadVectors()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return File.ReadLines(Path.Combine(root, "protocol", "v1", "test-vectors", "protocol-v1.hex"))
            .Where(line => line.Length != 0 && !line.StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => Convert.FromHexString(parts[1]));
    }

    private sealed class FragmentedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class RecordingWriteStream : MemoryStream
    {
        public int MaximumWrite { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumWrite = Math.Max(MaximumWrite, buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
