using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using LiveCaptionsTranslator.worker;

namespace LiveCaptionsTranslator.ipc
{
    public sealed class NamedPipeWorkerTransportFactory : IAsrWorkerTransportFactory
    {
        private readonly TimeSpan connectionTimeout;
        private readonly TimeSpan readyTimeout;
        public NamedPipeWorkerTransportFactory(TimeSpan? connectionTimeout = null, TimeSpan? readyTimeout = null)
        {
            this.connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(5);
            this.readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(5);
        }
        public IAsrWorkerTransport Create(WorkerPipeIdentity identity) => new NamedPipeWorkerTransport(identity, connectionTimeout, readyTimeout);
    }

    public sealed record WorkerPipeIdentity(string ControlPipeName, string AudioPipeName, Guid SessionId, byte[] Nonce)
    {
        public static WorkerPipeIdentity Create()
        {
            static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            return new WorkerPipeIdentity($"lct-control-{Token()}", $"lct-audio-{Token()}", Guid.NewGuid(), RandomNumberGenerator.GetBytes(IpcProtocol.NonceBytes));
        }
    }

    public sealed class NamedPipeWorkerTransport : IAsrWorkerTransport
    {
        private readonly WorkerPipeIdentity identity;
        private readonly TimeSpan connectionTimeout;
        private readonly TimeSpan readyTimeout;
        private readonly NamedPipeServerStream controlServer;
        private readonly NamedPipeServerStream audioServer;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcMessage>> pending = new();
        private readonly CancellationTokenSource lifetime = new();
        private IpcMessageStream? control;
        private IpcMessageStream? audio;
        private Task? readerTask;
        private bool stopped;
        private long sent;
        private long received;

        public NamedPipeWorkerTransport(WorkerPipeIdentity identity, TimeSpan? connectionTimeout = null, TimeSpan? readyTimeout = null)
        {
            this.identity = identity;
            this.connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(5);
            this.readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(5);
            controlServer = Create(identity.ControlPipeName, PipeDirection.InOut);
            audioServer = Create(identity.AudioPipeName, PipeDirection.Out);
        }

        public event EventHandler<AudioStreamSummaryPayload>? ProgressReceived;
        public event EventHandler<ErrorPayload>? ErrorReceived;
        public long ControlMessagesSent => Interlocked.Read(ref sent);
        public long ControlMessagesReceived => Interlocked.Read(ref received);
        public DateTimeOffset? LatestPongAtUtc { get; private set; }

        private static NamedPipeServerStream Create(string name, PipeDirection direction) => new(name, direction, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 16 * 1024, 16 * 1024);

        public async Task<WorkerTransportStartResult> ConnectAndHandshakeAsync(int expectedPid, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(connectionTimeout);
            try { await Task.WhenAll(controlServer.WaitForConnectionAsync(connect.Token), audioServer.WaitForConnectionAsync(connect.Token)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Worker pipe connection timed out."); }
            control = new IpcMessageStream(controlServer);
            audio = new IpcMessageStream(audioServer, IpcProtocol.AudioFramePayloadSize);
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ready.CancelAfter(readyTimeout);
            var helloMessage = await control.ReadAsync(ready.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("Control pipe closed before WorkerHello.");
            Interlocked.Increment(ref received);
            if (helloMessage.Envelope.MessageType != IpcMessageType.WorkerHello) throw new IpcProtocolException(ProtocolFailureKind.OutOfOrder, "WorkerHello was required first.");
            var hello = ProtocolPayloadCodec.DecodeWorkerHello(helloMessage.Payload);
            var rejection = ValidateHello(hello, expectedPid);
            if (rejection != null)
            {
                await control.WriteAsync(IpcMessageType.HostReject, ProtocolPayloadCodec.Encode(rejection), helloMessage.Envelope.CorrelationId, cancellationToken: cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref sent);
                throw new IpcProtocolException(ProtocolFailureKind.InvalidPayload, $"Worker handshake rejected: {rejection.Reason}.");
            }
            var negotiated = Math.Min(IpcProtocol.Minor, hello.MaximumMinor);
            var accepted = new HostAcceptPayload(identity.SessionId, negotiated, 16000, 1, 16, 20, 320, 640);
            await control.WriteAsync(IpcMessageType.HostAccept, ProtocolPayloadCodec.Encode(accepted), helloMessage.Envelope.CorrelationId, cancellationToken: cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref sent);
            var readyMessage = await control.ReadAsync(ready.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("Control pipe closed before WorkerReady.");
            Interlocked.Increment(ref received);
            if (readyMessage.Envelope.MessageType != IpcMessageType.WorkerReady) throw new IpcProtocolException(ProtocolFailureKind.OutOfOrder, "WorkerReady was required after HostAccept.");
            var workerReady = ProtocolPayloadCodec.DecodeWorkerReady(readyMessage.Payload);
            if (workerReady.SessionId != identity.SessionId || workerReady.WorkerPid != expectedPid) throw new IpcProtocolException(ProtocolFailureKind.InvalidPayload, "WorkerReady identity does not match.");
            readerTask = ReadControlAsync(lifetime.Token);
            stopwatch.Stop();
            return new WorkerTransportStartResult(negotiated, hello.Capabilities, stopwatch.Elapsed);
        }

        private HostRejectPayload? ValidateHello(WorkerHelloPayload hello, int expectedPid)
        {
            if (hello.SessionId != identity.SessionId) return new(ProtocolRejectReason.SessionMismatch, "Worker session mismatch.");
            if (!CryptographicOperations.FixedTimeEquals(hello.Nonce, identity.Nonce)) return new(ProtocolRejectReason.AuthenticationFailed, "Authentication failed.");
            if (hello.WorkerPid != expectedPid) return new(ProtocolRejectReason.ProcessMismatch, "Worker PID mismatch.");
            if (hello.MinimumMinor > IpcProtocol.Minor || hello.MaximumMinor < hello.MinimumMinor) return new(ProtocolRejectReason.ProtocolMismatch, "No supported minor version.");
            var required = WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink;
            if ((hello.Capabilities & required) != required || (hello.Capabilities & (WorkerCapabilities.Vad | WorkerCapabilities.Whisper | WorkerCapabilities.Cuda | WorkerCapabilities.CaptionProduction)) != 0) return new(ProtocolRejectReason.CapabilityMismatch, "Worker capabilities are invalid for Stage 4.");
            return null;
        }

        public async Task StartAudioStreamAsync(StartAudioStreamPayload request, CancellationToken cancellationToken = default) =>
            _ = await RequestAsync(IpcMessageType.StartAudioStream, ProtocolPayloadCodec.Encode(request), IpcMessageType.AudioStreamStarted, cancellationToken).ConfigureAwait(false);

        public async Task SendAudioFrameAsync(Guid workerSessionId, audio.NormalizedAudioFrame frame, CancellationToken cancellationToken = default)
        {
            var stream = audio ?? throw new InvalidOperationException("Audio pipe is not connected.");
            await stream.WriteAsync(IpcMessageType.AudioFrame, ProtocolPayloadCodec.EncodeAudioFrame(workerSessionId, frame), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<AudioStreamSummaryPayload> StopAudioStreamAsync(Guid workerSessionId, Guid captureSessionId, CancellationToken cancellationToken = default)
        {
            var response = await RequestAsync(IpcMessageType.StopAudioStream, ProtocolPayloadCodec.Encode(new AudioStreamIdentityPayload(workerSessionId, captureSessionId)), IpcMessageType.AudioStreamStopped, cancellationToken).ConfigureAwait(false);
            return ProtocolPayloadCodec.DecodeAudioStreamSummary(response.Payload);
        }

        public async Task PingAsync(CancellationToken cancellationToken = default)
        {
            var response = await RequestAsync(IpcMessageType.Ping, ProtocolPayloadCodec.Encode(new HeartbeatPayload(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), IpcMessageType.Pong, cancellationToken).ConfigureAwait(false);
            _ = ProtocolPayloadCodec.DecodeHeartbeat(response.Payload); LatestPongAtUtc = DateTimeOffset.UtcNow;
        }

        public async Task<bool> ShutdownAsync(Guid workerSessionId, CancellationToken cancellationToken = default)
        {
            var response = await RequestAsync(IpcMessageType.Shutdown, ProtocolPayloadCodec.Encode(new ShutdownPayload(workerSessionId)), IpcMessageType.ShutdownAcknowledged, cancellationToken).ConfigureAwait(false);
            return ProtocolPayloadCodec.DecodeShutdown(response.Payload).WorkerSessionId == workerSessionId;
        }

        private async Task<IpcMessage> RequestAsync(IpcMessageType type, byte[] payload, IpcMessageType expected, CancellationToken cancellationToken)
        {
            var stream = control ?? throw new InvalidOperationException("Control pipe is not connected.");
            var correlation = Guid.NewGuid();
            var completion = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(correlation, completion)) throw new InvalidOperationException("Correlation collision.");
            try
            {
                await stream.WriteAsync(type, payload, correlation, cancellationToken: cancellationToken).ConfigureAwait(false); Interlocked.Increment(ref sent);
                var response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (response.Envelope.MessageType != expected) throw new IpcProtocolException(ProtocolFailureKind.OutOfOrder, $"Expected {expected}, received {response.Envelope.MessageType}.");
                return response;
            }
            finally { pending.TryRemove(correlation, out _); }
        }

        private async Task ReadControlAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = await control!.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (message == null) break;
                    Interlocked.Increment(ref received);
                    if (message.Envelope.CorrelationId != Guid.Empty && pending.TryGetValue(message.Envelope.CorrelationId, out var completion)) { completion.TrySetResult(message); continue; }
                    if (message.Envelope.MessageType == IpcMessageType.AudioProgress) InvokeSafely(ProgressReceived, ProtocolPayloadCodec.DecodeAudioStreamSummary(message.Payload));
                    else if (message.Envelope.MessageType == IpcMessageType.Error) InvokeSafely(ErrorReceived, ProtocolPayloadCodec.DecodeError(message.Payload));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* Expected transport stop. */ }
            catch (Exception ex) { foreach (var item in pending.Values) item.TrySetException(ex); }
            finally { foreach (var item in pending.Values) item.TrySetException(new EndOfStreamException("Control pipe closed.")); }
        }

        public async Task StopAsync()
        {
            if (stopped) return; stopped = true; lifetime.Cancel();
            if (readerTask != null) try { await readerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        private void InvokeSafely<T>(EventHandler<T>? handlers, T value)
        {
            foreach (EventHandler<T> handler in handlers?.GetInvocationList() ?? [])
            {
                try { handler(this, value); }
                catch (Exception ex) { Debug.WriteLine($"Worker transport subscriber failed: {ex}"); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            if (audio != null) await audio.DisposeAsync().ConfigureAwait(false); else await audioServer.DisposeAsync().ConfigureAwait(false);
            if (control != null) await control.DisposeAsync().ConfigureAwait(false); else await controlServer.DisposeAsync().ConfigureAwait(false);
            lifetime.Dispose();
        }
    }
}
