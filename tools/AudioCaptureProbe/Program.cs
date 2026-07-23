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
            await using var service = new AudioCaptureService(
                new WindowsAudioEndpointProvider(),
                new WasapiLoopbackCaptureRuntimeFactory());
            IAudioProbeWaveWriter? writer = wavPath == null
                ? null
                : new NormalizedWaveFileWriter(wavPath);
            var result = await AudioProbeRunner.RunAsync(
                service,
                string.Equals(device, "default", StringComparison.OrdinalIgnoreCase) ? null : device,
                TimeSpan.FromSeconds(durationSeconds),
                writer,
                ctrlC.Token);

            if (result.StartResult?.Success == true)
            {
                Console.WriteLine($"Endpoint: {result.StartResult.Endpoint!.DisplayName}");
                Console.WriteLine($"Endpoint ID: {result.StartResult.Endpoint.Id}");
                Console.WriteLine($"Session: {result.StartResult.SessionId}");
                Console.WriteLine($"Input: {result.StartedDiagnostics?.InputFormat}");
                Console.WriteLine($"Normalized: {NormalizedAudioFormat.Description}");
                if (!string.IsNullOrWhiteSpace(result.StartResult.Diagnostic))
                    Console.WriteLine($"Resolution: {result.StartResult.Diagnostic}");
            }
            Console.WriteLine($"Final state: {result.FinalDiagnostics.State}");
            Console.WriteLine($"Frames produced: {result.FinalDiagnostics.FramesProduced}");
            Console.WriteLine($"Frames consumed: {result.FinalDiagnostics.FramesConsumed}");
            Console.WriteLine($"Frames dropped: {result.FinalDiagnostics.FramesDropped}");
            Console.WriteLine($"Captured duration: {result.FinalDiagnostics.FramesConsumed * 0.02:F2} s");
            Console.WriteLine($"RMS: {result.Levels.Rms:F6}");
            Console.WriteLine($"Peak: {result.Levels.Peak:F6}");
            Console.WriteLine($"Failure: {result.FailureReason ?? "none"}");
            if (wavPath != null)
                Console.WriteLine($"WAV: {Path.GetFullPath(wavPath)}");

            return result.ExitCode;
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
