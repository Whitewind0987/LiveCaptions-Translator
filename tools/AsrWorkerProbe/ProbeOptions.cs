namespace LiveCaptionsTranslator.tools.asrworkerprobe;

internal sealed record ProbeOptions(
    string Worker,
    bool Synthetic,
    bool Audio,
    bool Restart,
    bool ControlledExit,
    bool SlowWorker,
    string? Device,
    int DurationSeconds,
    int? CancelAfterMilliseconds);

internal static class ProbeOptionsParser
{
    public static ProbeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? worker = null;
        string? device = "default";
        var deviceSpecified = false;
        var synthetic = false;
        var audio = false;
        var restart = false;
        var controlledExit = false;
        var slowWorker = false;
        var duration = 5;
        int? cancelAfter = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--worker": worker = NextValue(args, ref index, "--worker"); break;
                case "--synthetic": synthetic = true; break;
                case "--audio": audio = true; break;
                case "--restart": restart = true; break;
                case "--controlled-exit": controlledExit = true; break;
                case "--slow-worker": slowWorker = true; break;
                case "--device": device = NextValue(args, ref index, "--device"); deviceSpecified = true; break;
                case "--duration": duration = ParseInt32(NextValue(args, ref index, "--duration"), "--duration"); break;
                case "--cancel-after-ms": cancelAfter = ParseInt32(NextValue(args, ref index, "--cancel-after-ms"), "--cancel-after-ms"); break;
                default: throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(worker) || synthetic == audio || duration < 1)
            throw new ArgumentException("Specify --worker and exactly one of --synthetic or --audio with a positive duration.");
        if (cancelAfter <= 0)
            throw new ArgumentException("Cancellation delay must be positive.");
        if (restart && controlledExit)
            throw new ArgumentException("--restart and --controlled-exit cannot be used together.");
        if (slowWorker && audio)
            throw new ArgumentException("--slow-worker requires --synthetic.");
        if (deviceSpecified && synthetic)
            throw new ArgumentException("--device requires --audio.");

        return new(
            Path.GetFullPath(worker),
            synthetic,
            audio,
            restart,
            controlledExit,
            slowWorker,
            device,
            duration,
            cancelAfter);
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }

    private static int ParseInt32(string value, string option) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new ArgumentException($"{option} requires an integer value.");
}
