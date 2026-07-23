using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.windows;
using LiveCaptionsTranslator.ipc;
using LiveCaptionsTranslator.worker;

internal static class Program
{
    private sealed record Options(string Worker, bool Synthetic, bool Audio, bool Restart, bool ControlledExit, bool SlowWorker, string? Device, int DurationSeconds, int? CancelAfterMilliseconds);

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try { options = Parse(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); Usage(); return 2; }
        using var cancellation = new CancellationTokenSource();
        if (options.CancelAfterMilliseconds.HasValue)
            cancellation.CancelAfter(options.CancelAfterMilliseconds.Value);
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            return options.Audio
                ? await RunAudioAsync(options, cancellation.Token).ConfigureAwait(false)
                : await RunSyntheticAsync(options, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Console.WriteLine("Requested cancellation completed cleanly."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"Probe failure: {ex}"); return 1; }
    }

    private static async Task<int> RunSyntheticAsync(Options options, CancellationToken cancellationToken)
    {
        var previousDelay = Environment.GetEnvironmentVariable("LIVE_CAPTIONS_ASR_TEST_AUDIO_DELAY_MS");
        if (options.SlowWorker) Environment.SetEnvironmentVariable("LIVE_CAPTIONS_ASR_TEST_AUDIO_DELAY_MS", "5");
        try
        {
        var total = System.Diagnostics.Stopwatch.StartNew();
        await using var supervisor = new AsrWorkerSupervisor(options.Worker);
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

    private static async Task<int> RunAudioAsync(Options options, CancellationToken cancellationToken)
    {
        await using var capture = new AudioCaptureService(new WindowsAudioEndpointProvider(), new WasapiLoopbackCaptureRuntimeFactory());
        await using var supervisor = new AsrWorkerSupervisor(options.Worker);
        await using var pipeline = new AudioWorkerPipeline(capture, supervisor);
        await pipeline.StartAsync(options.Device is "default" ? null : options.Device, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds), cancellationToken).ConfigureAwait(false);
        await pipeline.StopAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = pipeline.Diagnostics;
        Console.WriteLine($"Capture frames: {diagnostics.Capture.FramesProduced}");
        Console.WriteLine($"Frames sent: {diagnostics.Pump?.FramesSent}");
        Console.WriteLine($"Worker frames: {diagnostics.WorkerSummary?.FramesReceived}");
        Console.WriteLine($"Sequence gaps: {diagnostics.WorkerSummary?.SequenceGaps}");
        Console.WriteLine($"Capture dropped: {diagnostics.Capture.FramesDropped}");
        Console.WriteLine($"Pump bytes: {diagnostics.Pump?.BytesSent}");
        Console.WriteLine($"Pump source gaps: {diagnostics.Pump?.SourceSequenceGaps}");
        Console.WriteLine($"Worker invalid frames: {diagnostics.WorkerSummary?.InvalidFrames}");
        var worker = supervisor.Diagnostics;
        Console.WriteLine($"Heartbeat failures: {worker.HeartbeatFailures}");
        Console.WriteLine($"Latest Pong: {worker.LatestPongAtUtc?.ToString("O") ?? "none"}");
        Console.WriteLine($"Job assigned: {worker.JobAssignmentSucceeded}");
        Console.WriteLine($"Graceful shutdown: {worker.GracefulShutdownSucceeded}");
        Console.WriteLine($"Forced termination: {worker.ForcedTerminationUsed}");
        Console.WriteLine($"Worker exit code: {worker.ProcessExitCode}");
        Console.WriteLine($"Failure kind: {diagnostics.FailureKind}");
        Console.WriteLine($"Failure: {diagnostics.FailureReason ?? "none"}");
        Console.WriteLine($"Cleanup failures: {(diagnostics.CleanupFailures.Count == 0 ? "none" : string.Join(" | ", diagnostics.CleanupFailures))}");
        Console.WriteLine($"Final state: {diagnostics.State}");
        var captureFrames = diagnostics.Capture.FramesProduced - diagnostics.Capture.FramesDropped;
        var sent = diagnostics.Pump?.FramesSent ?? -1;
        var received = diagnostics.WorkerSummary?.FramesReceived ?? -1;
        var stopBoundaryRemainder = Math.Max(0, captureFrames - sent);
        var validBoundary = stopBoundaryRemainder <= 3;
        return diagnostics.State == AudioWorkerPipelineState.Stopped && diagnostics.FailureKind == AsrWorkerFailureKind.None && diagnostics.Capture.FramesDropped == 0 && validBoundary && sent == received && diagnostics.Pump?.SourceSequenceGaps == 0 && diagnostics.WorkerSummary?.SequenceGaps == 0 && diagnostics.WorkerSummary?.InvalidFrames == 0 && worker.HeartbeatFailures == 0 && worker.JobAssignmentSucceeded && worker.GracefulShutdownSucceeded && !worker.ForcedTerminationUsed && worker.ProcessExitCode == 0 && diagnostics.CleanupFailures.Count == 0 ? 0 : 1;
    }

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

    private static Options Parse(string[] args)
    {
        string? worker = null, device = "default"; var synthetic = false; var audio = false; var restart = false; var controlled = false; var slowWorker = false; var duration = 5; int? cancelAfter = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--worker": worker = args[++i]; break;
                case "--synthetic": synthetic = true; break;
                case "--audio": audio = true; break;
                case "--restart": restart = true; break;
                case "--controlled-exit": controlled = true; break;
                case "--slow-worker": slowWorker = true; break;
                case "--device": device = args[++i]; break;
                case "--duration": duration = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--cancel-after-ms": cancelAfter = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(worker) || synthetic == audio || duration < 1) throw new ArgumentException("Specify --worker and exactly one of --synthetic or --audio with a positive duration.");
        if (cancelAfter <= 0) throw new ArgumentException("Cancellation delay must be positive.");
        if (slowWorker && !synthetic) throw new ArgumentException("--slow-worker requires --synthetic.");
        return new(Path.GetFullPath(worker), synthetic, audio, restart, controlled, slowWorker, device, duration, cancelAfter);
    }
    private static void Usage() => Console.Error.WriteLine("AsrWorkerProbe --worker <exe> (--synthetic|--audio) [--device default|id] [--duration N] [--restart|--controlled-exit] [--slow-worker] [--cancel-after-ms N]");
}
