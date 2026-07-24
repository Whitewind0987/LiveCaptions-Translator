using System.IO.Pipes;
using LiveCaptionsTranslator.ipc;
using LiveCaptionsTranslator.worker;
using LiveCaptionsTranslator.captioning;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class NamedPipeWorkerTransportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ValidTwoPipeHandshakeNegotiatesReady()
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var worker = RunWorkerHandshakeAsync(identity, identity.Nonce, Environment.ProcessId);
        var result = await host.ConnectAndHandshakeAsync(Environment.ProcessId, Token);
        await worker;
        Assert.Equal((ushort)0, result.NegotiatedMinor);
        Assert.Equal(WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink, result.Capabilities);
        await host.StopAsync();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InvalidNonceOrPidIsRejected(bool wrongNonce, bool wrongPid)
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var nonce = wrongNonce ? new byte[32] : identity.Nonce;
        var worker = RunWorkerHandshakeAsync(identity, nonce, wrongPid ? Environment.ProcessId + 1 : Environment.ProcessId, expectReject: true);
        var exception = await Assert.ThrowsAsync<WorkerTransportException>(() => host.ConnectAndHandshakeAsync(Environment.ProcessId, Token));
        Assert.Equal(AsrWorkerFailureKind.HandshakeRejected, exception.Kind);
        await worker;
    }

    [Fact]
    public async Task ConnectionTimeoutIsBounded()
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40));
        var exception = await Assert.ThrowsAsync<WorkerTransportException>(() => host.ConnectAndHandshakeAsync(Environment.ProcessId, Token));
        Assert.Equal(AsrWorkerFailureKind.ControlPipeTimeout, exception.Kind);
    }

    [Fact]
    public async Task CancellationDuringConnectionIsObserved()
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ConnectAndHandshakeAsync(Environment.ProcessId, cancellation.Token));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task InvalidAudioPipeIdentityCannotAuthenticateOrReceiveHostAudio(bool wrongNonce, bool wrongSession, bool wrongPid)
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var worker = RunSplitIdentityHandshakeAsync(identity, wrongNonce, wrongSession, wrongPid);
        var exception = await Assert.ThrowsAsync<WorkerTransportException>(() => host.ConnectAndHandshakeAsync(Environment.ProcessId, Token));
        Assert.Equal(AsrWorkerFailureKind.HandshakeRejected, exception.Kind);
        Assert.False(await worker);
    }

    [Fact]
    public async Task AudioConnectionTimeoutIsDistinguished()
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
        await using var control = new NamedPipeClientStream(".", identity.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connecting = control.ConnectAsync(1000, Token);
        var exception = await Assert.ThrowsAsync<WorkerTransportException>(() => host.ConnectAndHandshakeAsync(Environment.ProcessId, Token));
        await connecting;
        Assert.Equal(AsrWorkerFailureKind.AudioPipeTimeout, exception.Kind);
    }

    [Fact]
    public async Task WorkerReadyTimeoutIsDistinguished()
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50));
        var worker = RunWorkerHandshakeAsync(identity, identity.Nonce, Environment.ProcessId, omitReady: true);
        var exception = await Assert.ThrowsAsync<WorkerTransportException>(() => host.ConnectAndHandshakeAsync(Environment.ProcessId, Token));
        await worker;
        Assert.Equal(AsrWorkerFailureKind.WorkerReadyTimeout, exception.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownCorrelationOrUnsolicitedKnownMessageIsProtocolViolation(bool unknownCorrelation)
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var worker = RunPostHandshakeMessageAsync(identity, unknownCorrelation);
        await host.ConnectAndHandshakeAsync(Environment.ProcessId, Token);
        var failure = await host.TerminalFailure.WaitAsync(TimeSpan.FromSeconds(1), Token);
        await worker;
        Assert.Equal(AsrWorkerFailureKind.ProtocolViolation, failure.Kind);
    }

    [Fact]
    public async Task StartAudioAcknowledgmentIdentityIsValidated()
    {
        var identity = WorkerPipeIdentity.Create(); var capture = Guid.NewGuid();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var worker = RunBadStartAcknowledgmentAsync(identity);
        await host.ConnectAndHandshakeAsync(Environment.ProcessId, Token);
        var exception = await Assert.ThrowsAsync<WorkerTransportException>(() => host.StartAudioStreamAsync(new(identity.SessionId, capture, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), Token));
        await worker;
        Assert.Equal(AsrWorkerFailureKind.ProtocolViolation, exception.Kind);
    }

    [Fact]
    public async Task ControlClosureImmediatelyCompletesTypedTerminalFailure()
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var worker = RunWorkerHandshakeAsync(identity, identity.Nonce, Environment.ProcessId);
        await host.ConnectAndHandshakeAsync(Environment.ProcessId, Token);
        await worker;
        var failure = await host.TerminalFailure.WaitAsync(TimeSpan.FromSeconds(1), Token);
        Assert.Equal(AsrWorkerFailureKind.ControlPipeClosed, failure.Kind);
    }

    [Fact]
    public async Task WorkerErrorImmediatelyCompletesTypedTerminalFailure()
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var worker = RunWorkerErrorAsync(identity);
        await host.ConnectAndHandshakeAsync(Environment.ProcessId, Token);
        var failure = await host.TerminalFailure.WaitAsync(TimeSpan.FromSeconds(1), Token);
        await worker;
        Assert.Equal(AsrWorkerFailureKind.WorkerReportedError, failure.Kind);
        Assert.Contains("worker failure", failure.Reason);
    }

    [Fact]
    public async Task UnsolicitedCaptionEventIsDecodedAndSubscriberFailuresAreIsolated()
    {
        var identity = WorkerPipeIdentity.Create(); var capture = Guid.NewGuid();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        CaptionEvent? received = null;
        host.CaptionEventReceived += (_, _) => throw new InvalidOperationException("subscriber");
        host.CaptionEventReceived += (_, value) => received = value;
        var worker = RunCaptionMessageAsync(identity, ProtocolPayloadCodec.EncodeCaptionEvent(
            new CaptionEvent(1, capture, 1, 0, 0, CaptionEventKind.Reset, "", null, null, DateTimeOffset.UtcNow)));
        await host.ConnectAndHandshakeAsync(Environment.ProcessId, Token);
        await worker;
        Assert.NotNull(received); Assert.Equal(capture, received.SessionId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CorrelatedOrMalformedCaptionEventIsProtocolViolation(bool correlated, bool malformed)
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var payload = malformed ? new byte[] { 1, 2, 3 } : ProtocolPayloadCodec.EncodeCaptionEvent(
            new CaptionEvent(1, Guid.NewGuid(), 1, 0, 0, CaptionEventKind.Reset, "", null, null, DateTimeOffset.UtcNow));
        var worker = RunCaptionMessageAsync(identity, payload, correlated);
        await host.ConnectAndHandshakeAsync(Environment.ProcessId, Token);
        var failure = await host.TerminalFailure.WaitAsync(TimeSpan.FromSeconds(1), Token);
        await worker;
        Assert.Equal(AsrWorkerFailureKind.ProtocolViolation, failure.Kind);
    }

    [Fact]
    public async Task SecondClientCannotJoinEitherOwnedSessionPipe()
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var handshake = host.ConnectAndHandshakeAsync(Environment.ProcessId, cancellation.Token);
        await using var firstControl = new NamedPipeClientStream(".", identity.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var firstAudio = new NamedPipeClientStream(".", identity.AudioPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(firstControl.ConnectAsync(1000, Token), firstAudio.ConnectAsync(1000, Token));
        await using var secondControl = new NamedPipeClientStream(".", identity.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Assert.ThrowsAsync<TimeoutException>(() => secondControl.ConnectAsync(50, Token));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handshake);
    }

    private static async Task RunWorkerHandshakeAsync(WorkerPipeIdentity identity, byte[] nonce, int pid, bool expectReject = false, bool omitReady = false)
    {
        await using var controlPipe = new NamedPipeClientStream(".", identity.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var audioPipe = new NamedPipeClientStream(".", identity.AudioPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(controlPipe.ConnectAsync(2000, Token), audioPipe.ConnectAsync(2000, Token));
        await using var control = new IpcMessageStream(controlPipe);
        await using var audio = new IpcMessageStream(audioPipe, IpcProtocol.AudioFramePayloadSize);
        var correlation = Guid.NewGuid();
        await control.WriteAsync(IpcMessageType.WorkerHello, ProtocolPayloadCodec.Encode(new WorkerHelloPayload(identity.SessionId, nonce, pid, 0, 0, "test", WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink)), correlation, cancellationToken: Token);
        await audio.WriteAsync(IpcMessageType.AudioPipeHello, ProtocolPayloadCodec.Encode(new AudioPipeHelloPayload(identity.SessionId, nonce, pid)), correlation, cancellationToken: Token);
        var response = await control.ReadAsync(Token);
        if (expectReject)
        {
            Assert.Equal(IpcMessageType.HostReject, response!.Envelope.MessageType);
            return;
        }
        Assert.Equal(IpcMessageType.HostAccept, response!.Envelope.MessageType);
        var audioAccepted = await audio.ReadAsync(Token);
        Assert.Equal(IpcMessageType.AudioPipeAccepted, audioAccepted!.Envelope.MessageType);
        if (omitReady) { await Task.Delay(100, Token); return; }
        await control.WriteAsync(IpcMessageType.WorkerReady, ProtocolPayloadCodec.Encode(new WorkerReadyPayload(identity.SessionId, pid)), correlation, cancellationToken: Token);
    }

    private static async Task<bool> RunSplitIdentityHandshakeAsync(WorkerPipeIdentity identity, bool wrongNonce, bool wrongSession, bool wrongPid)
    {
        await using var controlPipe = new NamedPipeClientStream(".", identity.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var audioPipe = new NamedPipeClientStream(".", identity.AudioPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(controlPipe.ConnectAsync(2000, Token), audioPipe.ConnectAsync(2000, Token));
        await using var control = new IpcMessageStream(controlPipe); await using var audio = new IpcMessageStream(audioPipe, IpcProtocol.AudioFramePayloadSize);
        var correlation = Guid.NewGuid();
        await control.WriteAsync(IpcMessageType.WorkerHello, ProtocolPayloadCodec.Encode(new WorkerHelloPayload(identity.SessionId, identity.Nonce, Environment.ProcessId, 0, 0, "test", WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink)), correlation, cancellationToken: Token);
        var audioNonce = wrongNonce ? new byte[IpcProtocol.NonceBytes] : identity.Nonce;
        var audioSession = wrongSession ? Guid.NewGuid() : identity.SessionId;
        var audioPid = wrongPid ? Environment.ProcessId + 1 : Environment.ProcessId;
        await audio.WriteAsync(IpcMessageType.AudioPipeHello, ProtocolPayloadCodec.Encode(new AudioPipeHelloPayload(audioSession, audioNonce, audioPid)), Guid.NewGuid(), cancellationToken: Token);
        var rejection = await control.ReadAsync(Token);
        Assert.Equal(IpcMessageType.HostReject, rejection!.Envelope.MessageType);
        using var timeout = new CancellationTokenSource(50);
        try { return await audio.ReadAsync(timeout.Token) != null; } catch (OperationCanceledException) { return false; }
    }

    private static async Task RunPostHandshakeMessageAsync(WorkerPipeIdentity identity, bool unknownCorrelation)
    {
        await using var controlPipe = new NamedPipeClientStream(".", identity.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var audioPipe = new NamedPipeClientStream(".", identity.AudioPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(controlPipe.ConnectAsync(2000, Token), audioPipe.ConnectAsync(2000, Token));
        await using var control = new IpcMessageStream(controlPipe); await using var audio = new IpcMessageStream(audioPipe, IpcProtocol.AudioFramePayloadSize);
        var correlation = Guid.NewGuid();
        await control.WriteAsync(IpcMessageType.WorkerHello, ProtocolPayloadCodec.Encode(new WorkerHelloPayload(identity.SessionId, identity.Nonce, Environment.ProcessId, 0, 0, "test", WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink)), correlation, cancellationToken: Token);
        await audio.WriteAsync(IpcMessageType.AudioPipeHello, ProtocolPayloadCodec.Encode(new AudioPipeHelloPayload(identity.SessionId, identity.Nonce, Environment.ProcessId)), correlation, cancellationToken: Token);
        await audio.ReadAsync(Token); await control.ReadAsync(Token);
        await control.WriteAsync(IpcMessageType.WorkerReady, ProtocolPayloadCodec.Encode(new WorkerReadyPayload(identity.SessionId, Environment.ProcessId)), correlation, cancellationToken: Token);
        await Task.Delay(20, Token);
        await control.WriteAsync(unknownCorrelation ? IpcMessageType.Pong : IpcMessageType.WorkerStatus, unknownCorrelation ? ProtocolPayloadCodec.Encode(new HeartbeatPayload(1)) : ProtocolPayloadCodec.Encode(new WorkerStatusPayload(1, "unexpected")), unknownCorrelation ? Guid.NewGuid() : Guid.Empty, cancellationToken: Token);
        await Task.Delay(20, Token);
    }

    private static async Task RunBadStartAcknowledgmentAsync(WorkerPipeIdentity identity)
    {
        await using var controlPipe = new NamedPipeClientStream(".", identity.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var audioPipe = new NamedPipeClientStream(".", identity.AudioPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(controlPipe.ConnectAsync(2000, Token), audioPipe.ConnectAsync(2000, Token));
        await using var control = new IpcMessageStream(controlPipe); await using var audio = new IpcMessageStream(audioPipe, IpcProtocol.AudioFramePayloadSize);
        var correlation = Guid.NewGuid();
        await control.WriteAsync(IpcMessageType.WorkerHello, ProtocolPayloadCodec.Encode(new WorkerHelloPayload(identity.SessionId, identity.Nonce, Environment.ProcessId, 0, 0, "test", WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink)), correlation, cancellationToken: Token);
        await audio.WriteAsync(IpcMessageType.AudioPipeHello, ProtocolPayloadCodec.Encode(new AudioPipeHelloPayload(identity.SessionId, identity.Nonce, Environment.ProcessId)), correlation, cancellationToken: Token);
        await audio.ReadAsync(Token); await control.ReadAsync(Token);
        await control.WriteAsync(IpcMessageType.WorkerReady, ProtocolPayloadCodec.Encode(new WorkerReadyPayload(identity.SessionId, Environment.ProcessId)), correlation, cancellationToken: Token);
        var start = await control.ReadAsync(Token);
        var request = ProtocolPayloadCodec.DecodeStartAudioStream(start!.Payload);
        await control.WriteAsync(IpcMessageType.AudioStreamStarted, ProtocolPayloadCodec.Encode(new AudioStreamIdentityPayload(identity.SessionId, Guid.NewGuid())), start.Envelope.CorrelationId, cancellationToken: Token);
        _ = request;
    }

    private static async Task RunWorkerErrorAsync(WorkerPipeIdentity identity)
    {
        await using var controlPipe = new NamedPipeClientStream(".", identity.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var audioPipe = new NamedPipeClientStream(".", identity.AudioPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(controlPipe.ConnectAsync(2000, Token), audioPipe.ConnectAsync(2000, Token));
        await using var control = new IpcMessageStream(controlPipe); await using var audio = new IpcMessageStream(audioPipe, IpcProtocol.AudioFramePayloadSize);
        var correlation = Guid.NewGuid();
        await control.WriteAsync(IpcMessageType.WorkerHello, ProtocolPayloadCodec.Encode(new WorkerHelloPayload(identity.SessionId, identity.Nonce, Environment.ProcessId, 0, 0, "test", WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink)), correlation, cancellationToken: Token);
        await audio.WriteAsync(IpcMessageType.AudioPipeHello, ProtocolPayloadCodec.Encode(new AudioPipeHelloPayload(identity.SessionId, identity.Nonce, Environment.ProcessId)), correlation, cancellationToken: Token);
        await audio.ReadAsync(Token); await control.ReadAsync(Token);
        await control.WriteAsync(IpcMessageType.WorkerReady, ProtocolPayloadCodec.Encode(new WorkerReadyPayload(identity.SessionId, Environment.ProcessId)), correlation, cancellationToken: Token);
        await Task.Delay(20, Token);
        await control.WriteAsync(IpcMessageType.Error, ProtocolPayloadCodec.Encode(new ErrorPayload(WorkerErrorKind.InternalFailure, "worker failure")), cancellationToken: Token);
        await Task.Delay(20, Token);
    }

    private static async Task RunCaptionMessageAsync(WorkerPipeIdentity identity, byte[] payload, bool correlated = false)
    {
        await using var controlPipe = new NamedPipeClientStream(".", identity.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var audioPipe = new NamedPipeClientStream(".", identity.AudioPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(controlPipe.ConnectAsync(2000, Token), audioPipe.ConnectAsync(2000, Token));
        await using var control = new IpcMessageStream(controlPipe); await using var audio = new IpcMessageStream(audioPipe, IpcProtocol.AudioFramePayloadSize);
        var handshake = Guid.NewGuid();
        await control.WriteAsync(IpcMessageType.WorkerHello, ProtocolPayloadCodec.Encode(new WorkerHelloPayload(identity.SessionId, identity.Nonce, Environment.ProcessId, 0, 0, "test", WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink)), handshake, cancellationToken: Token);
        await audio.WriteAsync(IpcMessageType.AudioPipeHello, ProtocolPayloadCodec.Encode(new AudioPipeHelloPayload(identity.SessionId, identity.Nonce, Environment.ProcessId)), handshake, cancellationToken: Token);
        await audio.ReadAsync(Token); await control.ReadAsync(Token);
        await control.WriteAsync(IpcMessageType.WorkerReady, ProtocolPayloadCodec.Encode(new WorkerReadyPayload(identity.SessionId, Environment.ProcessId)), handshake, cancellationToken: Token);
        await Task.Delay(20, Token);
        await control.WriteAsync(IpcMessageType.CaptionEvent, payload, correlated ? Guid.NewGuid() : Guid.Empty, cancellationToken: Token);
        await Task.Delay(30, Token);
    }
}
