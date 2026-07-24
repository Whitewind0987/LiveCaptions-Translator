using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.windows;
using LiveCaptionsTranslator.ipc;
using LiveCaptionsTranslator.worker;
using LiveCaptionsTranslator.captioning;

namespace LiveCaptionsTranslator.tools.asrworkerprobe;

internal static class Program
{
    private sealed record AudioSessionSnapshot(
        string Label,
        Guid WorkerSessionId,
        int WorkerProcessId,
        AudioWorkerPipelineDiagnostics Pipeline,
        AsrWorkerDiagnostics Worker);

    public static async Task<int> Main(string[] args)
    {
        ProbeOptions options;
        try { options = ProbeOptionsParser.Parse(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); Usage(); return 2; }
        using var cancellation = new CancellationTokenSource();
        if (options.CancelAfterMilliseconds.HasValue)
            cancellation.CancelAfter(options.CancelAfterMilliseconds.Value);
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (options.Wav != null) return await RunWaveAsync(options, cancellation.Token).ConfigureAwait(false);
            return options.Audio ? await RunAudioAsync(options, cancellation.Token).ConfigureAwait(false)
                : await RunSyntheticAsync(options, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Console.WriteLine("Requested cancellation completed cleanly."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"Probe failure: {ex}"); return 1; }
    }

    private static async Task<int> RunSyntheticAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var previousDelay = Environment.GetEnvironmentVariable("LIVE_CAPTIONS_ASR_TEST_AUDIO_DELAY_MS");
        if (options.SlowWorker) Environment.SetEnvironmentVariable("LIVE_CAPTIONS_ASR_TEST_AUDIO_DELAY_MS", "5");
        try
        {
        var total = System.Diagnostics.Stopwatch.StartNew();
        await using var supervisor = new AsrWorkerSupervisor(options.Worker, recognition: CreateRecognition(options));
        var result = await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.FailureReason);
        var originalSession = result.SessionId!.Value;
        var generated = 0L;
        var workerReceived = 0L;
        var workerGaps = 0L;
        AudioStreamSummaryPayload? summary = null;
        if (options.ControlledExit)
        {
            await supervisor.TerminateOwnedWorkerForDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            await WaitForStateAsync(supervisor, AsrWorkerState.Faulted, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            await WaitForDiagnosticsAsync(supervisor, diagnostics => diagnostics.ProcessExitCode.HasValue && supervisor.ActiveTransport == null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            summary = await StreamSyntheticAsync(supervisor, options.DurationSeconds, options.SlowWorker, () => generated++, cancellationToken).ConfigureAwait(false);
            workerReceived += summary.FramesReceived;
            workerGaps += summary.SequenceGaps;
            if (options.Restart)
            {
                var restarted = await supervisor.RestartAsync(cancellationToken).ConfigureAwait(false);
                if (!restarted.Success || restarted.SessionId == originalSession) throw new InvalidOperationException("Explicit restart did not create a new worker session.");
                summary = await StreamSyntheticAsync(supervisor, 1, options.SlowWorker, () => generated++, cancellationToken).ConfigureAwait(false);
                workerReceived += summary.FramesReceived;
                workerGaps += summary.SequenceGaps;
            }
        }
        var beforeStop = supervisor.Diagnostics;
        await supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
        var final = supervisor.Diagnostics;
        Console.WriteLine($"Worker path: {options.Worker}");
        Console.WriteLine($"Worker PID: {result.ProcessId}");
        Console.WriteLine($"Worker session: {originalSession}");
        Console.WriteLine($"Negotiated protocol: {beforeStop.NegotiatedProtocol}");
        Console.WriteLine($"Capabilities: {beforeStop.Capabilities}");
        Console.WriteLine($"Handshake latency: {(beforeStop.HandshakeCompletedAtUtc - beforeStop.HandshakeStartedAtUtc)?.TotalMilliseconds:F1} ms");
        Console.WriteLine($"Generated frames: {generated}");
        Console.WriteLine($"Worker frames received: {workerReceived}");
        Console.WriteLine($"Worker sequence gaps: {workerGaps}");
        Console.WriteLine($"Heartbeat failures: {beforeStop.HeartbeatFailures}");
        Console.WriteLine($"Latest Pong: {beforeStop.LatestPongAtUtc?.ToString("O") ?? "none"}");
        Console.WriteLine($"Audio frames sent: {beforeStop.AudioFramesSent}");
        Console.WriteLine($"Audio bytes sent: {beforeStop.AudioBytesSent}");
        Console.WriteLine($"Job assigned: {final.JobAssignmentSucceeded}");
        Console.WriteLine($"Shutdown acknowledged: {final.GracefulShutdownSucceeded}");
        Console.WriteLine($"Forced termination: {final.ForcedTerminationUsed}");
        Console.WriteLine($"Worker exit code: {final.ProcessExitCode}");
        Console.WriteLine($"Final state: {final.State}");
        Console.WriteLine($"Failure kind: {final.FailureKind}");
        Console.WriteLine($"Failure reason: {final.FailureReason ?? "none"}");
        Console.WriteLine($"Cleanup failures: {(final.CleanupFailures.Count == 0 ? "none" : string.Join(" | ", final.CleanupFailures))}");
        Console.WriteLine($"Total duration: {total.Elapsed.TotalSeconds:F2} s");
        var expectedCapabilities = WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink;
        if (!options.ControlledExit && (summary == null || workerReceived != generated || workerGaps != 0 || summary.InvalidFrames != 0 || !final.GracefulShutdownSucceeded || final.ForcedTerminationUsed || final.ProcessExitCode != 0 || final.CleanupFailures.Count != 0 || beforeStop.Capabilities != expectedCapabilities)) return 1;
        if (!options.ControlledExit && options.DurationSeconds >= 5 && beforeStop.LatestPongAtUtc == null) return 1;
        if (options.ControlledExit && (beforeStop.State != AsrWorkerState.Faulted || beforeStop.FailureKind != AsrWorkerFailureKind.WorkerExited || beforeStop.CleanupFailures.Count != 0 || beforeStop.ForcedTerminationUsed || beforeStop.ProcessExitCode == null)) return 1;
        return 0;
        }
        finally { Environment.SetEnvironmentVariable("LIVE_CAPTIONS_ASR_TEST_AUDIO_DELAY_MS", previousDelay); }
    }

    private static async Task<AudioStreamSummaryPayload> StreamSyntheticAsync(AsrWorkerSupervisor supervisor, int seconds, bool slowWorker, Action generated, CancellationToken cancellationToken)
    {
        var transport = supervisor.ActiveTransport ?? throw new InvalidOperationException("Transport missing.");
        var workerSession = supervisor.SessionId!.Value; var capture = Guid.NewGuid(); var frames = Math.Max(1, seconds * 50);
        await transport.StartAudioStreamAsync(new StartAudioStreamPayload(workerSession, capture, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
        await supervisor.SetStreamingAsync(true, cancellationToken).ConfigureAwait(false);
        var started = DateTimeOffset.UtcNow;
        for (var i = 0; i < frames; i++)
        {
            var payload = new byte[NormalizedAudioFormat.BytesPerFrame];
            for (var p = 0; p < payload.Length; p++) payload[p] = (byte)((i + p) & 0xff);
            var frame = new NormalizedAudioFrame(capture, i + 1, i * NormalizedAudioFormat.SamplesPerFrame, started.AddMilliseconds(i * 20), payload);
            await transport.SendAudioFrameAsync(workerSession, frame, cancellationToken).ConfigureAwait(false); generated();
            if (!slowWorker) await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
        await transport.EndAudioStreamAsync(new AudioStreamEndPayload(workerSession, capture, frames, frames * NormalizedAudioFormat.BytesPerFrame, 1, frames, 0), cancellationToken).ConfigureAwait(false);
        var summary = await transport.StopAudioStreamAsync(workerSession, capture, cancellationToken).ConfigureAwait(false);
        await supervisor.SetStreamingAsync(false, cancellationToken).ConfigureAwait(false);
        return summary;
    }

    private static async Task<int> RunAudioAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var capture = new AudioCaptureService(new WindowsAudioEndpointProvider(), new WasapiLoopbackCaptureRuntimeFactory());
        var supervisor = new AsrWorkerSupervisor(options.Worker, recognition: CreateRecognition(options));
        var pipeline = new AudioWorkerPipeline(capture, supervisor);
        var captionGate = new CaptionEventGate();
        var captionEvents = new List<CaptionEvent>();
        var captionRejected = false;
        pipeline.CaptionEventReceived += (_, captionEvent) =>
        {
            var accepted = captionGate.TryAccept(captionEvent, out var _rejection);
            captionRejected |= !accepted;
            if (captionEvents.Count < 256) captionEvents.Add(captionEvent);
            PrintCaption(captionEvent, accepted);
        };
        var sessions = new List<AudioSessionSnapshot>();
        var disposalFailures = new List<Exception>();
        Exception? operationFailure = null;
        var requestedCancellation = false;
        Guid activeWorkerSession = Guid.Empty;
        var activeWorkerPid = 0;

        try
        {
            try
            {
                if (options.ControlledExit)
                {
                    (activeWorkerSession, activeWorkerPid) = await StartAudioSessionAsync(pipeline, supervisor, options, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(options.DurationSeconds, 3)), cancellationToken).ConfigureAwait(false);
                    await supervisor.TerminateOwnedWorkerForDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                    await WaitForStateAsync(pipeline, AudioWorkerPipelineState.Faulted, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    await WaitForAudioCleanupAsync(pipeline, supervisor, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                else if (options.Restart)
                {
                    var halfDuration = TimeSpan.FromSeconds(options.DurationSeconds / 2d);
                    (activeWorkerSession, activeWorkerPid) = await StartAudioSessionAsync(pipeline, supervisor, options, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(halfDuration, cancellationToken).ConfigureAwait(false);
                    await pipeline.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    sessions.Add(Snapshot("First real-audio session", activeWorkerSession, activeWorkerPid, pipeline, supervisor));

                    var restarted = await supervisor.RestartAsync(cancellationToken).ConfigureAwait(false);
                    if (!restarted.Success || restarted.SessionId == null || restarted.ProcessId == null)
                        throw new InvalidOperationException(restarted.FailureReason ?? "Explicit worker restart failed.");
                    activeWorkerSession = restarted.SessionId.Value;
                    activeWorkerPid = restarted.ProcessId.Value;
                    await pipeline.StartAsync(options.Device is "default" ? null : options.Device, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(halfDuration, cancellationToken).ConfigureAwait(false);
                    await pipeline.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    (activeWorkerSession, activeWorkerPid) = await StartAudioSessionAsync(pipeline, supervisor, options, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds), cancellationToken).ConfigureAwait(false);
                    await pipeline.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                requestedCancellation = true;
            }
            catch (Exception ex)
            {
                operationFailure = ex;
            }
        }
        finally
        {
            await DisposeAudioOwnersAsync(pipeline, supervisor, capture, disposalFailures).ConfigureAwait(false);
        }

        sessions.Add(Snapshot(
            options.Restart ? "Second real-audio session" : options.ControlledExit ? "Controlled-exit real-audio session" : "Real-audio session",
            activeWorkerSession,
            activeWorkerPid,
            pipeline,
            supervisor));
        foreach (var session in sessions)
            PrintAudioDiagnostics(session);
        Console.WriteLine($"Disposal failures: {(disposalFailures.Count == 0 ? "none" : string.Join(" | ", disposalFailures.Select(DescribeException)))}");

        if (operationFailure != null)
        {
            Console.Error.WriteLine($"Real-audio operation failed: {operationFailure}");
            return 1;
        }
        if (disposalFailures.Count != 0)
            return 1;

        var final = sessions[^1];
        if (requestedCancellation)
        {
            var accepted = IsCleanCanceledAudioSession(final, disposalFailures.Count != 0);
            if (accepted) Console.WriteLine("Requested cancellation completed cleanly.");
            return accepted ? 0 : 1;
        }
        if (options.ControlledExit)
            return IsExpectedControlledExit(final) ? 0 : 1;
        if (options.Restart)
        {
            var first = sessions[0];
            var second = sessions[1];
            var firstAccepted = IsCleanAudioSession(first, requireAudio: true);
            var secondAccepted = IsCleanAudioSession(second, requireAudio: true);
            var firstWorkerExited = first.Worker.State == AsrWorkerState.Stopped &&
                first.Worker.ProcessExitCode.HasValue && first.Worker.WorkerPid == null;
            return ProbeAcceptance.IsValidExplicitRestart(
                first.WorkerSessionId,
                first.WorkerProcessId,
                firstWorkerExited,
                firstAccepted,
                second.WorkerSessionId,
                second.WorkerProcessId,
                secondAccepted) ? 0 : 1;
        }
        var recognitionAccepted = !options.Recognition || (!captionRejected && captionEvents.FirstOrDefault()?.Kind == CaptionEventKind.Reset && captionEvents.Any(value => value.Kind == CaptionEventKind.Final && !string.IsNullOrWhiteSpace(value.Text)));
        return IsCleanAudioSession(final, requireAudio: true) && recognitionAccepted ? 0 : 1;
    }

    private static WorkerRecognitionConfiguration CreateRecognition(ProbeOptions options) => options.Recognition
        ? WorkerRecognitionConfiguration.Create(options.VadModel!, options.WhisperModel!, options.Language, options.Threads)
        : WorkerRecognitionConfiguration.Disabled;

    private static async Task<int> RunWaveAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var fixture = WaveFixtureReader.Read(options.Wav!);
        var events = new List<CaptionEvent>();
        var gate = new CaptionEventGate();
        var rejected = false;
        var recognition = CreateRecognition(options);
        await using var supervisor = new AsrWorkerSupervisor(options.Worker, recognition: recognition);
        var started = await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Success) throw new WorkerTransportException(started.FailureKind, started.FailureReason ?? "Recognition worker failed to start.");
        var transport = supervisor.ActiveTransport ?? throw new InvalidOperationException("Worker transport is unavailable.");
        transport.CaptionEventReceived += (_, captionEvent) =>
        {
            var accepted = gate.TryAccept(captionEvent, out var _rejection);
            rejected |= !accepted;
            if (events.Count < 256) events.Add(captionEvent);
            PrintCaption(captionEvent, accepted);
        };
        var capture = Guid.NewGuid();
        var worker = started.SessionId!.Value;
        await transport.StartAudioStreamAsync(new StartAudioStreamPayload(worker, capture, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
        await supervisor.SetStreamingAsync(true, cancellationToken).ConfigureAwait(false);
        var timestamp = DateTimeOffset.UtcNow;
        for (var index = 0; index < fixture.FrameCount; ++index)
        {
            var pcm = fixture.Pcm.AsSpan(index * 640, 640).ToArray();
            var frame = new NormalizedAudioFrame(capture, index + 1, index * 320L, timestamp.AddMilliseconds(index * 20L), pcm);
            await transport.SendAudioFrameAsync(worker, frame, cancellationToken).ConfigureAwait(false);
        }
        var frames = fixture.FrameCount;
        await transport.EndAudioStreamAsync(new AudioStreamEndPayload(worker, capture, frames, frames * 640L, 1, frames, 0), cancellationToken).ConfigureAwait(false);
        var summary = await transport.StopAudioStreamAsync(worker, capture, cancellationToken).ConfigureAwait(false);
        await supervisor.SetStreamingAsync(false, cancellationToken).ConfigureAwait(false);
        var beforeStop = supervisor.Diagnostics;
        await supervisor.StopAsync(CancellationToken.None).ConfigureAwait(false);
        var final = supervisor.Diagnostics;
        Console.WriteLine($"WAV frames: {frames}; final-frame padding: {fixture.PaddedBytes} bytes");
        Console.WriteLine($"Worker summary: {summary.FramesReceived} frames, {summary.PcmBytesReceived} bytes, {summary.SequenceGaps} gaps, {summary.InvalidFrames} invalid");
        Console.WriteLine($"Capabilities: {beforeStop.Capabilities}; heartbeat failures: {beforeStop.HeartbeatFailures}");
        Console.WriteLine($"Final worker state: {final.State}; graceful: {final.GracefulShutdownSucceeded}; forced: {final.ForcedTerminationUsed}; exit: {final.ProcessExitCode}");
        foreach (var line in final.RecentStdout) Console.WriteLine($"Worker diagnostic: {line}");
        var finals = events.Where(value => value.Kind == CaptionEventKind.Final).ToArray();
        foreach (var value in finals) Console.WriteLine($"Final transcript: {value.Text}");
        var tokensMatch = options.ExpectedTokens.All(token => finals.Any(value => ExpectedCaptionMatcher.Contains(value.Text, token)));
        var capabilities = WorkerCapabilities.ProtocolV1 | WorkerCapabilities.NormalizedPcmSink | WorkerCapabilities.Vad | WorkerCapabilities.Whisper | WorkerCapabilities.CaptionProduction;
        var finalRequirementMet = options.ExpectedTokens.Count == 0 || finals.Length != 0;
        return !rejected && events.FirstOrDefault()?.Kind == CaptionEventKind.Reset && finalRequirementMet && tokensMatch &&
            summary.FramesReceived == frames && summary.PcmBytesReceived == frames * 640L && summary.SequenceGaps == 0 && summary.InvalidFrames == 0 &&
            beforeStop.Capabilities == capabilities && beforeStop.HeartbeatFailures == 0 && final.GracefulShutdownSucceeded &&
            !final.ForcedTerminationUsed && final.ProcessExitCode == 0 && final.CleanupFailures.Count == 0 && final.WorkerPid == null ? 0 : 1;
    }

    private static void PrintCaption(CaptionEvent value, bool accepted) => Console.WriteLine(
        $"CaptionEvent session={value.SessionId:D} sequence={value.Sequence} segment={value.SegmentId} revision={value.Revision} kind={value.Kind} audio={value.AudioStartMilliseconds?.ToString() ?? "-"}-{value.AudioEndMilliseconds?.ToString() ?? "-"} accepted={accepted} text={value.Text}");

    private static async Task<(Guid SessionId, int ProcessId)> StartAudioSessionAsync(
        AudioWorkerPipeline pipeline,
        AsrWorkerSupervisor supervisor,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        await pipeline.StartAsync(options.Device is "default" ? null : options.Device, cancellationToken).ConfigureAwait(false);
        var diagnostics = supervisor.Diagnostics;
        return (
            supervisor.SessionId ?? throw new InvalidOperationException("Worker session identity is unavailable after pipeline start."),
            diagnostics.WorkerPid ?? throw new InvalidOperationException("Owned worker PID is unavailable after pipeline start."));
    }

    private static AudioSessionSnapshot Snapshot(
        string label,
        Guid sessionId,
        int processId,
        AudioWorkerPipeline pipeline,
        AsrWorkerSupervisor supervisor) =>
        new(label, sessionId, processId, pipeline.Diagnostics, supervisor.Diagnostics);

    private static async Task DisposeAudioOwnersAsync(
        AudioWorkerPipeline pipeline,
        AsrWorkerSupervisor supervisor,
        AudioCaptureService capture,
        List<Exception> failures)
    {
        try { await pipeline.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(new InvalidOperationException($"Pipeline disposal failed: {DescribeException(ex)}", ex)); }
        try { await supervisor.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(new InvalidOperationException($"Supervisor disposal failed: {DescribeException(ex)}", ex)); }
        try { await capture.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(new InvalidOperationException($"Capture disposal failed: {DescribeException(ex)}", ex)); }
    }

    private static bool IsCleanAudioSession(AudioSessionSnapshot session, bool requireAudio)
    {
        var pipeline = session.Pipeline;
        var capture = pipeline.Capture;
        var pump = pipeline.Pump;
        var summary = pipeline.WorkerSummary;
        var worker = session.Worker;
        if (pump == null || summary == null) return false;
        var totalsMatch = capture.FramesProduced == capture.FramesConsumed &&
            capture.FramesConsumed == pump.FramesSent && pump.FramesSent == worker.AudioFramesSent &&
            worker.AudioFramesSent == summary.FramesReceived && pump.BytesSent == worker.AudioBytesSent &&
            worker.AudioBytesSent == summary.PcmBytesReceived;
        return (!requireAudio || capture.FramesProduced > 0) && totalsMatch && capture.FramesBuffered == 0 &&
            capture.FramesDropped == 0 && capture.State == AudioCaptureState.Stopped &&
            pipeline.State == AudioWorkerPipelineState.Stopped && pipeline.FailureKind == AsrWorkerFailureKind.None &&
            pipeline.CleanupFailures.Count == 0 && pipeline.PumpJoined && pump.Phase == AudioFramePumpPhase.Completed &&
            pump.SourceCompletionObserved && !pump.OwnedCancellationUsed && pump.SourceSequenceGaps == 0 &&
            summary.SequenceGaps == 0 && summary.InvalidFrames == 0 && worker.SequenceGaps == 0 &&
            worker.WorkerInvalidFrames == 0 && worker.HeartbeatFailures == 0 && worker.JobAssignmentSucceeded &&
            worker.State == AsrWorkerState.Stopped && worker.FailureKind == AsrWorkerFailureKind.None &&
            worker.GracefulShutdownSucceeded && !worker.ForcedTerminationUsed && worker.ProcessExitCode == 0 &&
            worker.CleanupFailures.Count == 0 && worker.WorkerPid == null;
    }

    private static bool IsCleanCanceledAudioSession(AudioSessionSnapshot session, bool hasDisposalFailures)
    {
        var pipeline = session.Pipeline;
        var capture = pipeline.Capture;
        var pump = pipeline.Pump;
        var summary = pipeline.WorkerSummary;
        var worker = session.Worker;
        if (pump == null || summary == null) return false;

        return ProbeAcceptance.IsCleanCanceledAudioSession(new CanceledAudioSessionFacts(
            capture.State == AudioCaptureState.Stopped,
            pipeline.State == AudioWorkerPipelineState.Stopped,
            pipeline.FailureKind != AsrWorkerFailureKind.None,
            pump.Phase == AudioFramePumpPhase.Completed,
            pipeline.PumpJoined,
            pump.SourceCompletionObserved,
            pump.OwnedCancellationUsed,
            capture.FramesProduced,
            capture.FramesConsumed,
            capture.FramesDropped,
            pump.FramesSent,
            worker.AudioFramesSent,
            summary.FramesReceived,
            pump.BytesSent,
            worker.AudioBytesSent,
            summary.PcmBytesReceived,
            pump.SourceSequenceGaps,
            summary.SequenceGaps,
            summary.InvalidFrames,
            worker.HeartbeatFailures,
            worker.GracefulShutdownSucceeded,
            worker.ForcedTerminationUsed,
            worker.ProcessExitCode,
            pipeline.CleanupFailures.Count != 0 || worker.CleanupFailures.Count != 0,
            hasDisposalFailures,
            worker.WorkerPid.HasValue));
    }

    private static bool IsExpectedControlledExit(AudioSessionSnapshot session)
    {
        var pipeline = session.Pipeline;
        var worker = session.Worker;
        return pipeline.State == AudioWorkerPipelineState.Faulted &&
            pipeline.FailureKind == AsrWorkerFailureKind.WorkerExited &&
            pipeline.Capture.State == AudioCaptureState.Stopped && pipeline.PumpJoined &&
            pipeline.CleanupFailures.Count == 0 && worker.State == AsrWorkerState.Stopped &&
            worker.FailureKind == AsrWorkerFailureKind.WorkerExited && worker.ProcessExitCode.HasValue &&
            !worker.ForcedTerminationUsed && worker.CleanupFailures.Count == 0 && worker.WorkerPid == null;
    }

    private static void PrintAudioDiagnostics(AudioSessionSnapshot session)
    {
        var diagnostics = session.Pipeline;
        var worker = session.Worker;
        Console.WriteLine($"--- {session.Label} ---");
        Console.WriteLine($"Worker session: {(session.WorkerSessionId == Guid.Empty ? "unavailable" : session.WorkerSessionId)}");
        Console.WriteLine($"Started worker PID: {(session.WorkerProcessId <= 0 ? "unavailable" : session.WorkerProcessId)}");
        Console.WriteLine($"Capture state: {diagnostics.Capture.State}");
        Console.WriteLine($"Capture frames produced: {diagnostics.Capture.FramesProduced}");
        Console.WriteLine($"Capture frames consumed: {diagnostics.Capture.FramesConsumed}");
        Console.WriteLine($"Capture frames buffered: {diagnostics.Capture.FramesBuffered}");
        Console.WriteLine($"Capture frames dropped: {diagnostics.Capture.FramesDropped}");
        Console.WriteLine($"Pump phase: {diagnostics.Pump?.Phase.ToString() ?? "unavailable"}");
        Console.WriteLine($"Pump joined: {diagnostics.PumpJoined}");
        Console.WriteLine($"Pump frames: {diagnostics.Pump?.FramesSent}");
        Console.WriteLine($"Pump bytes: {diagnostics.Pump?.BytesSent}");
        Console.WriteLine($"Pump current sequence: {diagnostics.Pump?.CurrentSequence?.ToString() ?? "none"}");
        Console.WriteLine($"Pump last sequence: {diagnostics.Pump?.LastSequence?.ToString() ?? "none"}");
        Console.WriteLine($"Pump source gaps: {diagnostics.Pump?.SourceSequenceGaps}");
        Console.WriteLine($"Pump source completion observed: {diagnostics.Pump?.SourceCompletionObserved}");
        Console.WriteLine($"Pump owned cancellation used: {diagnostics.Pump?.OwnedCancellationUsed}");
        Console.WriteLine($"Pump failure: {diagnostics.Pump?.FailureReason ?? "none"}");
        Console.WriteLine($"Transport audio frames: {worker.AudioFramesSent}");
        Console.WriteLine($"Transport audio bytes: {worker.AudioBytesSent}");
        Console.WriteLine($"Worker progress frames: {worker.WorkerFramesReceived}");
        Console.WriteLine($"Worker progress bytes: {worker.WorkerBytesReceived}");
        Console.WriteLine($"Latest worker progress: {worker.LatestProgressAtUtc?.ToString("O") ?? "none"}");
        Console.WriteLine($"Worker summary frames: {diagnostics.WorkerSummary?.FramesReceived}");
        Console.WriteLine($"Worker summary gaps: {diagnostics.WorkerSummary?.SequenceGaps}");
        Console.WriteLine($"Worker invalid frames: {diagnostics.WorkerSummary?.InvalidFrames}");
        Console.WriteLine($"Heartbeat failures: {worker.HeartbeatFailures}");
        Console.WriteLine($"Latest Pong: {worker.LatestPongAtUtc?.ToString("O") ?? "none"}");
        Console.WriteLine($"Job assigned: {worker.JobAssignmentSucceeded}");
        Console.WriteLine($"Graceful shutdown: {worker.GracefulShutdownSucceeded}");
        Console.WriteLine($"Forced termination: {worker.ForcedTerminationUsed}");
        Console.WriteLine($"Worker state: {worker.State}");
        Console.WriteLine($"Worker PID: {worker.WorkerPid?.ToString() ?? "none"}");
        Console.WriteLine($"Worker exit code: {worker.ProcessExitCode?.ToString() ?? "none"}");
        Console.WriteLine($"Worker failure kind: {worker.FailureKind}");
        Console.WriteLine($"Worker failure: {worker.FailureReason ?? "none"}");
        Console.WriteLine($"Pipeline failure kind: {diagnostics.FailureKind}");
        Console.WriteLine($"Pipeline failure: {diagnostics.FailureReason ?? "none"}");
        Console.WriteLine($"Cleanup failures: {(diagnostics.CleanupFailures.Count == 0 ? "none" : string.Join(" | ", diagnostics.CleanupFailures))}");
        Console.WriteLine($"Final pipeline state: {diagnostics.State}");
    }

    private static async Task WaitForStateAsync(
        AudioWorkerPipeline pipeline,
        AudioWorkerPipelineState state,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (pipeline.State != state && DateTimeOffset.UtcNow < until)
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        if (pipeline.State != state)
            throw new TimeoutException($"Audio pipeline did not reach {state}.");
    }

    private static async Task WaitForAudioCleanupAsync(
        AudioWorkerPipeline pipeline,
        AsrWorkerSupervisor supervisor,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            var pipelineDiagnostics = pipeline.Diagnostics;
            var workerDiagnostics = supervisor.Diagnostics;
            if (pipelineDiagnostics.PumpJoined && pipelineDiagnostics.Capture.State == AudioCaptureState.Stopped &&
                supervisor.ActiveTransport == null && workerDiagnostics.ProcessExitCode.HasValue)
                return;
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Real-audio controlled-exit cleanup did not complete.");
    }

    private static string DescribeException(Exception exception) =>
        exception is AggregateException aggregate
            ? string.Join(" | ", aggregate.Flatten().InnerExceptions.Select(DescribeException))
            : exception.Message;

    private static async Task WaitForStateAsync(AsrWorkerSupervisor supervisor, AsrWorkerState state, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (supervisor.State != state && DateTimeOffset.UtcNow < until) await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        if (supervisor.State != state) throw new TimeoutException($"Worker did not reach {state}.");
    }

    private static async Task WaitForDiagnosticsAsync(AsrWorkerSupervisor supervisor, Func<AsrWorkerDiagnostics, bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (!condition(supervisor.Diagnostics) && DateTimeOffset.UtcNow < until) await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        if (!condition(supervisor.Diagnostics)) throw new TimeoutException("Worker cleanup diagnostics did not settle.");
    }

    private static void Usage() => Console.Error.WriteLine("AsrWorkerProbe --worker <exe> (--synthetic|--audio|--wav <pcm16.wav>) [--device default|id] [--duration N] [--restart|--controlled-exit] [--slow-worker] [--cancel-after-ms N] [--recognition --vad-model <absolute.onnx> --whisper-model <absolute.bin> [--language auto|code] [--threads N] [--expected-token text]]");
}
