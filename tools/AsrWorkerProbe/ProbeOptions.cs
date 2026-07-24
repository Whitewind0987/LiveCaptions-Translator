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
    int? CancelAfterMilliseconds,
    bool Recognition,
    string? VadModel,
    string? WhisperModel,
    string Language,
    int? Threads,
    string? Wav,
    IReadOnlyList<string> ExpectedTokens);

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
        var recognition = false;
        string? vadModel = null;
        string? whisperModel = null;
        var language = "auto";
        int? threads = null;
        string? wav = null;
        var expectedTokens = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option != "--expected-token" && !unique.Add(option)) throw new ArgumentException($"Duplicate argument '{option}'.");
            switch (option)
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
                case "--recognition": recognition = true; break;
                case "--vad-model": vadModel = NextValue(args, ref index, "--vad-model"); break;
                case "--whisper-model": whisperModel = NextValue(args, ref index, "--whisper-model"); break;
                case "--language": language = NextValue(args, ref index, "--language"); break;
                case "--threads": threads = ParseInt32(NextValue(args, ref index, "--threads"), "--threads"); break;
                case "--wav": wav = NextValue(args, ref index, "--wav"); break;
                case "--expected-token": expectedTokens.Add(NextValue(args, ref index, "--expected-token")); break;
                default: throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        var modes = (synthetic ? 1 : 0) + (audio ? 1 : 0) + (wav != null ? 1 : 0);
        if (string.IsNullOrWhiteSpace(worker) || modes != 1 || duration < 1)
            throw new ArgumentException("Specify --worker and exactly one of --synthetic, --audio, or --wav with a positive duration.");
        if (cancelAfter <= 0)
            throw new ArgumentException("Cancellation delay must be positive.");
        if (restart && controlledExit)
            throw new ArgumentException("--restart and --controlled-exit cannot be used together.");
        if (slowWorker && audio)
            throw new ArgumentException("--slow-worker requires --synthetic.");
        if (deviceSpecified && synthetic)
            throw new ArgumentException("--device requires --audio.");
        if (deviceSpecified && wav != null) throw new ArgumentException("--device requires --audio.");
        if (recognition != (vadModel != null || whisperModel != null))
            throw new ArgumentException("--recognition requires both --vad-model and --whisper-model, and model options require --recognition.");
        if (recognition && (vadModel == null || whisperModel == null))
            throw new ArgumentException("--recognition requires both --vad-model and --whisper-model.");
        if (!recognition && (threads != null || language != "auto" || expectedTokens.Count != 0 || wav != null))
            throw new ArgumentException("Recognition-only options require --recognition.");
        if (wav != null && (restart || controlledExit || slowWorker || cancelAfter != null))
            throw new ArgumentException("--wav does not support restart, controlled exit, slow worker, or cancellation options.");

        return new(
            Path.GetFullPath(worker),
            synthetic,
            audio,
            restart,
            controlledExit,
            slowWorker,
            device,
            duration,
            cancelAfter,
            recognition,
            vadModel == null ? null : Path.GetFullPath(vadModel),
            whisperModel == null ? null : Path.GetFullPath(whisperModel),
            language,
            threads,
            wav == null ? null : Path.GetFullPath(wav),
            expectedTokens);
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
