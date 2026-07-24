using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using LiveCaptionsTranslator.worker;
using LiveCaptionsTranslator.captioning;

namespace LiveCaptionsTranslator.ipc
{
    public sealed class NamedPipeWorkerTransportFactory : IAsrWorkerTransportFactory
    {
        private readonly TimeSpan connectionTimeout;
        private readonly TimeSpan readyTimeout;
        private readonly bool recognitionEnabled;

        public NamedPipeWorkerTransportFactory(bool recognitionEnabled = false, TimeSpan? connectionTimeout = null, TimeSpan? readyTimeout = null)
        {
            this.recognitionEnabled = recognitionEnabled;
            this.connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(5);
            this.readyTimeout = readyTimeout ?? (recognitionEnabled ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(5));
        }

        public NamedPipeWorkerTransportFactory(TimeSpan? connectionTimeout, TimeSpan? readyTimeout)
            : this(false, connectionTimeout, readyTimeout) { }

        public IAsrWorkerTransport Create(WorkerPipeIdentity identity) => new NamedPipeWorkerTransport(identity, recognitionEnabled, connectionTimeout, readyTimeout);
    }

    public sealed record WorkerPipeIdentity(string ControlPipeName, string AudioPipeName, Guid SessionId, byte[] Nonce)
    {
        public static WorkerPipeIdentity Create()
        {
            static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            return new($"lct-control-{Token()}", $"lct-audio-{Token()}", Guid.NewGuid(), RandomNumberGenerator.GetBytes(IpcProtocol.NonceBytes));
        }
    }

    public sealed class NamedPipeWorkerTransport : IAsrWorkerTransport
    {
        private readonly WorkerPipeIdentity identity;
        private readonly TimeSpan connectionTimeout;
        private readonly TimeSpan readyTimeout;
        private readonly bool recognitionEnabled;
        private readonly NamedPipeServerStream controlServer;
        private readonly NamedPipeServerStream audioServer;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcMessage>> pending = new();
        private readonly CancellationTokenSource lifetime = new();
        private readonly TaskCompletionSource<WorkerTransportFailure> terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim stopGate = new(1, 1);
        private IpcMessageStream? control;
        private IpcMessageStream? audio;
        private Task? readerTask;
        private Guid? activeCaptureSession;
        private bool audioStreamEnded;
        private bool stopped;
        private bool disposed;
        private long sent;
        private long received;
        private long audioFramesSent;
        private long audioBytesSent;
        private long streamFramesSent;
        private long streamBytesSent;
        private long streamFirstSequence;
        private long streamLastSequence;
        private long streamGaps;
        private long streamInitialSequence;

        public NamedPipeWorkerTransport(WorkerPipeIdentity identity, bool recognitionEnabled = false, TimeSpan? connectionTimeout = null, TimeSpan? readyTimeout = null)
        {
            this.identity = identity;
            this.recognitionEnabled = recognitionEnabled;
            this.connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(5);
            this.readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(5);
            controlServer = Create(identity.ControlPipeName);
            audioServer = Create(identity.AudioPipeName);
        }

        public NamedPipeWorkerTransport(WorkerPipeIdentity identity, TimeSpan? connectionTimeout, TimeSpan? readyTimeout)
            : this(identity, false, connectionTimeout, readyTimeout) { }

        public event EventHandler<AudioStreamSummaryPayload>? ProgressReceived;
        public event EventHandler<ErrorPayload>? ErrorReceived;
        public event EventHandler<CaptionEvent>? CaptionEventReceived;
        public long ControlMessagesSent => Interlocked.Read(ref sent);
        public long ControlMessagesReceived => Interlocked.Read(ref received);
        public long AudioFramesSent => Interlocked.Read(ref audioFramesSent);
        public long AudioBytesSent => Interlocked.Read(ref audioBytesSent);
        public DateTimeOffset? LatestPongAtUtc { get; private set; }
        public Task<WorkerTransportFailure> TerminalFailure => terminal.Task;

        private static NamedPipeServerStream Create(string name) => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 16 * 1024, 16 * 1024);

        public async Task<WorkerTransportStartResult> ConnectAndHandshakeAsync(int expectedPid, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            await WaitForConnectionAsync(controlServer, AsrWorkerFailureKind.ControlPipeTimeout, cancellationToken).ConfigureAwait(false);
            await WaitForConnectionAsync(audioServer, AsrWorkerFailureKind.AudioPipeTimeout, cancellationToken).ConfigureAwait(false);
            control = new IpcMessageStream(controlServer);
            audio = new IpcMessageStream(audioServer, IpcProtocol.AudioFramePayloadSize);

            var helloMessage = await ReadHandshakeAsync(control, AsrWorkerFailureKind.HandshakeRejected, "WorkerHello", cancellationToken).ConfigureAwait(false);
            if (helloMessage.Envelope.MessageType != IpcMessageType.WorkerHello)
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "WorkerHello was required first.");
            var hello = ProtocolPayloadCodec.DecodeWorkerHello(helloMessage.Payload);
            var rejection = ValidateHello(hello, expectedPid);

            var audioHelloMessage = await ReadHandshakeAsync(audio, AsrWorkerFailureKind.HandshakeRejected, "AudioPipeHello", cancellationToken).ConfigureAwait(false);
            if (audioHelloMessage.Envelope.MessageType != IpcMessageType.AudioPipeHello)
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "AudioPipeHello was required before audio was accepted.");
            var audioHello = ProtocolPayloadCodec.DecodeAudioPipeHello(audioHelloMessage.Payload);
            rejection ??= ValidateAudioHello(audioHello, expectedPid);

            if (rejection != null)
            {
                await WriteControlAsync(IpcMessageType.HostReject, ProtocolPayloadCodec.Encode(rejection), helloMessage.Envelope.CorrelationId, cancellationToken).ConfigureAwait(false);
                throw new WorkerTransportException(rejection.Reason == ProtocolRejectReason.ProtocolMismatch ? AsrWorkerFailureKind.ProtocolMismatch : AsrWorkerFailureKind.HandshakeRejected, $"Worker handshake rejected: {rejection.Reason}.");
            }

            await WriteAudioAsync(IpcMessageType.AudioPipeAccepted, ProtocolPayloadCodec.Encode(new AudioPipeAcceptedPayload(identity.SessionId, expectedPid)), audioHelloMessage.Envelope.CorrelationId, cancellationToken).ConfigureAwait(false);
            var negotiated = Math.Min(IpcProtocol.Minor, hello.MaximumMinor);
            var accepted = new HostAcceptPayload(identity.SessionId, negotiated, 16000, 1, 16, 20, 320, 640);
            await WriteControlAsync(IpcMessageType.HostAccept, ProtocolPayloadCodec.Encode(accepted), helloMessage.Envelope.CorrelationId, cancellationToken).ConfigureAwait(false);

            IpcMessage readyMessage;
            try { readyMessage = await ReadWithTimeoutAsync(control, readyTimeout, cancellationToken).ConfigureAwait(false) ?? throw new EndOfStreamException("Control pipe closed before WorkerReady."); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new WorkerTransportException(AsrWorkerFailureKind.WorkerReadyTimeout, "WorkerReady timed out."); }
            catch (EndOfStreamException ex) { throw new WorkerTransportException(AsrWorkerFailureKind.ControlPipeClosed, ex.Message, ex); }
            Interlocked.Increment(ref received);
            if (readyMessage.Envelope.MessageType == IpcMessageType.Error && readyMessage.Envelope.CorrelationId == Guid.Empty)
            {
                var error = ProtocolPayloadCodec.DecodeError(readyMessage.Payload);
                throw new WorkerTransportException(MapWorkerError(error.Kind), $"Worker error {error.Kind}: {error.Diagnostic}");
            }
            if (readyMessage.Envelope.MessageType != IpcMessageType.WorkerReady || readyMessage.Envelope.CorrelationId != helloMessage.Envelope.CorrelationId)
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "WorkerReady type or correlation is invalid.");
            var workerReady = ProtocolPayloadCodec.DecodeWorkerReady(readyMessage.Payload);
            if (workerReady.SessionId != identity.SessionId || workerReady.WorkerPid != expectedPid)
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "WorkerReady identity does not match.");

            readerTask = ReadControlAsync(lifetime.Token);
            stopwatch.Stop();
            return new(negotiated, hello.Capabilities, stopwatch.Elapsed);
        }

        private async Task WaitForConnectionAsync(NamedPipeServerStream server, AsrWorkerFailureKind kind, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(connectionTimeout);
            try { await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new WorkerTransportException(kind, kind == AsrWorkerFailureKind.ControlPipeTimeout ? "Control pipe connection timed out." : "Audio pipe connection timed out."); }
        }

        private async Task<IpcMessage> ReadHandshakeAsync(IpcMessageStream stream, AsrWorkerFailureKind kind, string phase, CancellationToken cancellationToken)
        {
            try
            {
                var message = await ReadWithTimeoutAsync(stream, readyTimeout, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref received);
                return message ?? throw new WorkerTransportException(kind, $"Pipe closed before {phase}.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new WorkerTransportException(kind, $"{phase} timed out."); }
            catch (IpcProtocolException ex) { throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, ex.Message, ex); }
        }

        private static async Task<IpcMessage?> ReadWithTimeoutAsync(IpcMessageStream stream, TimeSpan timeoutValue, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutValue);
            return await stream.ReadAsync(timeout.Token).ConfigureAwait(false);
        }

        private HostRejectPayload? ValidateHello(WorkerHelloPayload hello, int expectedPid)
        {
            if (hello.SessionId != identity.SessionId) return new(ProtocolRejectReason.SessionMismatch, "Worker session mismatch.");
            if (!CryptographicOperations.FixedTimeEquals(hello.Nonce, identity.Nonce)) return new(ProtocolRejectReason.AuthenticationFailed, "Authentication failed.");
            if (hello.WorkerPid != expectedPid) return new(ProtocolRejectReason.ProcessMismatch, "Worker PID mismatch.");
            if (hello.MinimumMinor > IpcProtocol.Minor || hello.MaximumMinor < hello.MinimumMinor) return new(ProtocolRejectReason.ProtocolMismatch, "No supported minor version.");
            var baseCapabilities = WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink;
            var recognitionCapabilities = WorkerCapabilities.Vad | WorkerCapabilities.Whisper | WorkerCapabilities.CaptionProduction;
            var required = recognitionEnabled ? baseCapabilities | recognitionCapabilities : baseCapabilities;
            if ((hello.Capabilities & required) != required || (hello.Capabilities & WorkerCapabilities.Cuda) != 0 ||
                (!recognitionEnabled && (hello.Capabilities & recognitionCapabilities) != 0))
                return new(ProtocolRejectReason.CapabilityMismatch, recognitionEnabled
                    ? "Worker capabilities do not match recognition mode."
                    : "Worker capabilities are invalid for transport-only mode.");
            return null;
        }

        private HostRejectPayload? ValidateAudioHello(AudioPipeHelloPayload hello, int expectedPid)
        {
            if (hello.SessionId != identity.SessionId) return new(ProtocolRejectReason.SessionMismatch, "Audio pipe session mismatch.");
            if (!CryptographicOperations.FixedTimeEquals(hello.Nonce, identity.Nonce)) return new(ProtocolRejectReason.AuthenticationFailed, "Audio pipe authentication failed.");
            if (hello.WorkerPid != expectedPid) return new(ProtocolRejectReason.ProcessMismatch, "Audio pipe PID mismatch.");
            return null;
        }

        public async Task StartAudioStreamAsync(StartAudioStreamPayload request, CancellationToken cancellationToken = default)
        {
            var response = await RequestAsync(IpcMessageType.StartAudioStream, ProtocolPayloadCodec.Encode(request), IpcMessageType.AudioStreamStarted, cancellationToken).ConfigureAwait(false);
            var accepted = ProtocolPayloadCodec.DecodeAudioStreamIdentity(response.Payload);
            if (accepted.WorkerSessionId != request.WorkerSessionId || accepted.CaptureSessionId != request.CaptureSessionId)
                throw Fail(AsrWorkerFailureKind.ProtocolViolation, "AudioStreamStarted identity does not match the request.");
            activeCaptureSession = request.CaptureSessionId;
            audioStreamEnded = false;
            streamFramesSent = streamBytesSent = streamFirstSequence = streamLastSequence = streamGaps = 0;
            streamInitialSequence = request.InitialFrameSequence;
        }

        public async Task SendAudioFrameAsync(Guid workerSessionId, audio.NormalizedAudioFrame frame, CancellationToken cancellationToken = default)
        {
            if (activeCaptureSession != frame.SessionId) throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "Audio stream is not active for this capture session.");
            if (audioStreamEnded) throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "Audio frames are not allowed after AudioStreamEnd.");
            try
            {
                await WriteAudioAsync(IpcMessageType.AudioFrame, ProtocolPayloadCodec.EncodeAudioFrame(workerSessionId, frame), Guid.Empty, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref audioFramesSent);
                Interlocked.Add(ref audioBytesSent, frame.Payload.Length);
                if (streamFramesSent == 0) { streamFirstSequence = frame.Sequence; streamGaps = frame.Sequence - streamInitialSequence; }
                else streamGaps += frame.Sequence - streamLastSequence - 1;
                streamLastSequence = frame.Sequence;
                streamFramesSent++;
                streamBytesSent += frame.Payload.Length;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw Fail(AsrWorkerFailureKind.AudioPipeClosed, $"Audio pipe write failed: {ex.Message}", ex);
            }
        }

        public async Task EndAudioStreamAsync(AudioStreamEndPayload end, CancellationToken cancellationToken = default)
        {
            if (activeCaptureSession != end.CaptureSessionId || end.WorkerSessionId != identity.SessionId)
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "AudioStreamEnd identity does not match the active stream.");
            if (audioStreamEnded) throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "AudioStreamEnd was already sent.");
            if (end.FramesSent != streamFramesSent || end.PcmBytesSent != streamBytesSent || end.FirstSequence != streamFirstSequence || end.FinalSequence != streamLastSequence || end.SourceSequenceGaps != streamGaps)
                throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "AudioStreamEnd totals do not match the transmitted audio frames.");
            try { await WriteAudioAsync(IpcMessageType.AudioStreamEnd, ProtocolPayloadCodec.Encode(end), Guid.Empty, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            { throw Fail(AsrWorkerFailureKind.AudioPipeClosed, $"AudioStreamEnd write failed: {ex.Message}", ex); }
            audioStreamEnded = true;
        }

        public async Task<AudioStreamSummaryPayload> StopAudioStreamAsync(Guid workerSessionId, Guid captureSessionId, CancellationToken cancellationToken = default)
        {
            if (!audioStreamEnded) throw new WorkerTransportException(AsrWorkerFailureKind.ProtocolViolation, "AudioStreamEnd is required before StopAudioStream.");
            var response = await RequestAsync(IpcMessageType.StopAudioStream, ProtocolPayloadCodec.Encode(new AudioStreamIdentityPayload(workerSessionId, captureSessionId)), IpcMessageType.AudioStreamStopped, cancellationToken).ConfigureAwait(false);
            var summary = ProtocolPayloadCodec.DecodeAudioStreamSummary(response.Payload);
            if (summary.CaptureSessionId != captureSessionId) throw Fail(AsrWorkerFailureKind.ProtocolViolation, "AudioStreamStopped capture session does not match.");
            activeCaptureSession = null;
            audioStreamEnded = false;
            return summary;
        }

        public async Task PingAsync(CancellationToken cancellationToken = default)
        {
            var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var response = await RequestAsync(IpcMessageType.Ping, ProtocolPayloadCodec.Encode(new HeartbeatPayload(sentAt)), IpcMessageType.Pong, cancellationToken).ConfigureAwait(false);
            if (ProtocolPayloadCodec.DecodeHeartbeat(response.Payload).SentAtUnixMilliseconds != sentAt)
                throw Fail(AsrWorkerFailureKind.ProtocolViolation, "Pong payload does not match Ping.");
            LatestPongAtUtc = DateTimeOffset.UtcNow;
        }

        public async Task<bool> ShutdownAsync(Guid workerSessionId, CancellationToken cancellationToken = default)
        {
            var response = await RequestAsync(IpcMessageType.Shutdown, ProtocolPayloadCodec.Encode(new ShutdownPayload(workerSessionId)), IpcMessageType.ShutdownAcknowledged, cancellationToken).ConfigureAwait(false);
            if (ProtocolPayloadCodec.DecodeShutdown(response.Payload).WorkerSessionId != workerSessionId)
                throw Fail(AsrWorkerFailureKind.ProtocolViolation, "ShutdownAcknowledged session does not match.");
            return true;
        }

        private async Task<IpcMessage> RequestAsync(IpcMessageType type, byte[] payload, IpcMessageType expected, CancellationToken cancellationToken)
        {
            var stream = control ?? throw new WorkerTransportException(AsrWorkerFailureKind.ControlPipeClosed, "Control pipe is not connected.");
            var correlation = Guid.NewGuid();
            var completion = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(correlation, completion)) throw new InvalidOperationException("Correlation collision.");
            try
            {
                try { await stream.WriteAsync(type, payload, correlation, cancellationToken: cancellationToken).ConfigureAwait(false); Interlocked.Increment(ref sent); }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { throw Fail(AsrWorkerFailureKind.ControlPipeClosed, $"Control pipe write failed: {ex.Message}", ex); }
                var response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (response.Envelope.MessageType != expected) throw Fail(AsrWorkerFailureKind.ProtocolViolation, $"Expected {expected}, received {response.Envelope.MessageType}.");
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
                    if (message == null) { if (!stopped) Fail(AsrWorkerFailureKind.ControlPipeClosed, "Control pipe closed."); return; }
                    Interlocked.Increment(ref received);
                    if (message.Envelope.CorrelationId != Guid.Empty)
                    {
                        if (pending.TryGetValue(message.Envelope.CorrelationId, out var completion)) { completion.TrySetResult(message); continue; }
                        Fail(AsrWorkerFailureKind.ProtocolViolation, "A response used an unknown correlation ID."); return;
                    }
                    if (message.Envelope.MessageType == IpcMessageType.AudioProgress)
                    {
                        var progress = ProtocolPayloadCodec.DecodeAudioStreamSummary(message.Payload);
                        if (activeCaptureSession == null || progress.CaptureSessionId != activeCaptureSession) { Fail(AsrWorkerFailureKind.ProtocolViolation, "AudioProgress capture session does not match."); return; }
                        InvokeSafely(ProgressReceived, progress);
                    }
                    else if (message.Envelope.MessageType == IpcMessageType.Error)
                    {
                        var error = ProtocolPayloadCodec.DecodeError(message.Payload);
                        InvokeSafely(ErrorReceived, error);
                        Fail(MapWorkerError(error.Kind), $"Worker error {error.Kind}: {error.Diagnostic}");
                        return;
                    }
                    else if (message.Envelope.MessageType == IpcMessageType.CaptionEvent)
                    {
                        var captionEvent = ProtocolPayloadCodec.DecodeCaptionEvent(message.Payload);
                        InvokeSafely(CaptionEventReceived, captionEvent);
                    }
                    else { Fail(AsrWorkerFailureKind.ProtocolViolation, $"Unsolicited {message.Envelope.MessageType} is not allowed."); return; }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IpcProtocolException ex) { Fail(AsrWorkerFailureKind.ProtocolViolation, ex.Message, ex); }
            catch (Exception ex) { if (!stopped) Fail(AsrWorkerFailureKind.ControlPipeClosed, ex.Message, ex); }
            finally
            {
                var failure = terminal.Task.IsCompletedSuccessfully ? terminal.Task.Result : new WorkerTransportFailure(AsrWorkerFailureKind.ControlPipeClosed, "Control pipe reader stopped.");
                foreach (var item in pending.Values) item.TrySetException(new WorkerTransportException(failure.Kind, failure.Reason, failure.Exception));
            }
        }

        private static AsrWorkerFailureKind MapWorkerError(WorkerErrorKind kind) => kind switch
        {
            WorkerErrorKind.InvalidRecognitionConfiguration => AsrWorkerFailureKind.InvalidRecognitionConfiguration,
            WorkerErrorKind.ModelLoadFailed => AsrWorkerFailureKind.ModelLoadFailed,
            WorkerErrorKind.VadInferenceFailed => AsrWorkerFailureKind.VadInferenceFailed,
            WorkerErrorKind.WhisperInferenceFailed => AsrWorkerFailureKind.WhisperInferenceFailed,
            WorkerErrorKind.RecognitionDrainTimeout => AsrWorkerFailureKind.RecognitionDrainTimeout,
            _ => AsrWorkerFailureKind.WorkerReportedError
        };

        private WorkerTransportException Fail(AsrWorkerFailureKind kind, string reason, Exception? exception = null)
        {
            var failure = new WorkerTransportFailure(kind, reason, exception);
            terminal.TrySetResult(failure);
            try { lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
            foreach (var item in pending.Values) item.TrySetException(new WorkerTransportException(kind, reason, exception));
            return new WorkerTransportException(kind, reason, exception);
        }

        private async Task WriteControlAsync(IpcMessageType type, byte[] payload, Guid correlation, CancellationToken cancellationToken)
        {
            await control!.WriteAsync(type, payload, correlation, cancellationToken: cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref sent);
        }

        private Task WriteAudioAsync(IpcMessageType type, byte[] payload, Guid correlation, CancellationToken cancellationToken) =>
            audio!.WriteAsync(type, payload, correlation, cancellationToken: cancellationToken);

        public async Task StopAsync()
        {
            await stopGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (stopped) return;
                stopped = true;
                lifetime.Cancel();
                foreach (var item in pending.Values) item.TrySetCanceled();
                if (readerTask != null) try { await readerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            finally { stopGate.Release(); }
        }

        private void InvokeSafely<T>(EventHandler<T>? handlers, T value)
        {
            foreach (EventHandler<T> handler in handlers?.GetInvocationList() ?? [])
                try { handler(this, value); } catch (Exception ex) { Debug.WriteLine($"Worker transport subscriber failed: {ex}"); }
        }

        public async ValueTask DisposeAsync()
        {
            await stopGate.WaitAsync().ConfigureAwait(false);
            var failures = new List<Exception>();
            try
            {
                if (disposed) return;
                disposed = true;
                stopped = true;
                lifetime.Cancel();
                foreach (var item in pending.Values) item.TrySetCanceled();
                if (readerTask != null) try { await readerTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex) { failures.Add(ex); }
                if (audio != null) try { await audio.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
                else try { await audioServer.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
                if (control != null) try { await control.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
                else try { await controlServer.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
                try { lifetime.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            }
            finally { stopGate.Release(); }
            if (failures.Count != 0)
            {
                terminal.TrySetResult(new WorkerTransportFailure(AsrWorkerFailureKind.CleanupFailed, string.Join(" | ", failures.Select(failure => failure.Message)), new AggregateException(failures)));
                throw new AggregateException("Transport disposal failed.", failures);
            }
        }
    }
}
