using System.IO.Pipes;
using LiveCaptionsTranslator.ipc;
using LiveCaptionsTranslator.worker;
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
        await Assert.ThrowsAsync<IpcProtocolException>(() => host.ConnectAndHandshakeAsync(Environment.ProcessId, Token));
        await worker;
    }

    [Fact]
    public async Task ConnectionTimeoutIsBounded()
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAsync<TimeoutException>(() => host.ConnectAndHandshakeAsync(Environment.ProcessId, Token));
    }

    [Fact]
    public async Task CancellationDuringConnectionIsObserved()
    {
        var identity = WorkerPipeIdentity.Create();
        await using var host = new NamedPipeWorkerTransport(identity, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ConnectAndHandshakeAsync(Environment.ProcessId, cancellation.Token));
    }

    private static async Task RunWorkerHandshakeAsync(WorkerPipeIdentity identity, byte[] nonce, int pid, bool expectReject = false)
    {
        await using var controlPipe = new NamedPipeClientStream(".", identity.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var audioPipe = new NamedPipeClientStream(".", identity.AudioPipeName, PipeDirection.In, PipeOptions.Asynchronous);
        await Task.WhenAll(controlPipe.ConnectAsync(2000, Token), audioPipe.ConnectAsync(2000, Token));
        await using var control = new IpcMessageStream(controlPipe);
        var correlation = Guid.NewGuid();
        await control.WriteAsync(IpcMessageType.WorkerHello, ProtocolPayloadCodec.Encode(new WorkerHelloPayload(identity.SessionId, nonce, pid, 0, 0, "test", WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink)), correlation, cancellationToken: Token);
        var response = await control.ReadAsync(Token);
        if (expectReject)
        {
            Assert.Equal(IpcMessageType.HostReject, response!.Envelope.MessageType);
            return;
        }
        Assert.Equal(IpcMessageType.HostAccept, response!.Envelope.MessageType);
        await control.WriteAsync(IpcMessageType.WorkerReady, ProtocolPayloadCodec.Encode(new WorkerReadyPayload(identity.SessionId, pid)), correlation, cancellationToken: Token);
    }
}
