using System.Threading.Channels;

using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.diagnostics;
using LiveCaptionsTranslator.audio.windows;

return await AudioCaptureProbeProgram.RunAsync(args);

internal static class AudioCaptureProbeProgram
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
            return await ListAsync();

        var durationSeconds = ParseIntOption(args, "--duration", 10);
        var device = ParseOption(args, "--device") ?? "default";
        var wavPath = ParseOption(args, "--wav");
        if (durationSeconds <= 0)
        {
            Console.Error.WriteLine("--duration must be a positive number of seconds.");
            return 2;
        }

        using var ctrlC = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            ctrlC.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            using var duration = AudioProbeDuration.Create(
                TimeSpan.FromSeconds(durationSeconds),
                ctrlC.Token);
            await using var service = new AudioCaptureService(
                new WindowsAudioEndpointProvider(),
                new WasapiLoopbackCaptureRuntimeFactory());

            var result = await service.StartAsync(
                string.Equals(device, "default", StringComparison.OrdinalIgnoreCase) ? null : device,
                duration.Token);
            if (!result.Success)
            {
                Console.Error.WriteLine($"Capture start failed: {result.FailureReason}");
                return 1;
            }

            var startedDiagnostics = service.Diagnostics;
            Console.WriteLine($"Endpoint: {result.Endpoint!.DisplayName}");
            Console.WriteLine($"Endpoint ID: {result.Endpoint.Id}");
            Console.WriteLine($"Session: {result.SessionId}");
            Console.WriteLine($"Input: {startedDiagnostics.InputFormat}");
            Console.WriteLine($"Normalized: {NormalizedAudioFormat.Description}");
            if (!string.IsNullOrWhiteSpace(result.Diagnostic))
                Console.WriteLine($"Resolution: {result.Diagnostic}");

            NormalizedWaveFileWriter? writer = wavPath == null
                ? null
                : new NormalizedWaveFileWriter(wavPath);
            var levels = new AudioLevelAccumulator();
            try
            {
                while (!duration.IsCancellationRequested)
                {
                    NormalizedAudioFrame frame;
                    try
                    {
                        frame = await service.FrameBuffer.ReadAsync(duration.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }

                    levels.AddFrame(frame);
                    if (writer != null)
                        await writer.WriteAsync(frame, duration.Token);
                }
            }
            finally
            {
                if (writer != null)
                    await writer.DisposeAsync();
                var finalDiagnostics = service.Diagnostics;
                var finalLevels = levels.Snapshot();
                await service.StopAsync(CancellationToken.None);
                Console.WriteLine($"Frames produced: {finalDiagnostics.FramesProduced}");
                Console.WriteLine($"Frames consumed: {finalDiagnostics.FramesConsumed}");
                Console.WriteLine($"Frames dropped: {finalDiagnostics.FramesDropped}");
                Console.WriteLine($"Captured duration: {finalDiagnostics.FramesConsumed * 0.02:F2} s");
                Console.WriteLine($"RMS: {finalLevels.Rms:F6}");
                Console.WriteLine($"Peak: {finalLevels.Peak:F6}");
                Console.WriteLine($"Failure: {finalDiagnostics.LastFailureReason ?? "none"}");
                if (wavPath != null)
                    Console.WriteLine($"WAV: {Path.GetFullPath(wavPath)}");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Audio probe failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static async Task<int> ListAsync()
    {
        await using var provider = new WindowsAudioEndpointProvider();
        var result = await provider.EnumerateAsync();
        if (!result.Success)
        {
            Console.Error.WriteLine(result.FailureReason);
            return 1;
        }

        foreach (var endpoint in result.Endpoints)
        {
            Console.WriteLine(
                $"{(endpoint.IsDefault ? "[default]" : "[       ]")} " +
                $"{endpoint.DisplayName} | {endpoint.Id} | {endpoint.Availability}");
        }
        return 0;
    }

    private static string? ParseOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, value =>
            string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int ParseIntOption(string[] args, string name, int defaultValue) =>
        int.TryParse(ParseOption(args, name), out var value) ? value : defaultValue;
}
