using System.IO;

namespace LiveCaptionsTranslator.worker
{
    public sealed record WorkerRecognitionConfiguration
    {
        public const int MaximumThreadCount = 32;

        private WorkerRecognitionConfiguration(bool enabled, string? vadModelPath, string? whisperModelPath, string language, int threadCount)
        {
            Enabled = enabled;
            VadModelPath = vadModelPath;
            WhisperModelPath = whisperModelPath;
            Language = language;
            ThreadCount = threadCount;
        }

        public bool Enabled { get; }
        public string? VadModelPath { get; }
        public string? WhisperModelPath { get; }
        public string Language { get; }
        public int ThreadCount { get; }

        public static WorkerRecognitionConfiguration Disabled { get; } =
            new(false, null, null, "auto", DefaultThreadCount);

        public static int DefaultThreadCount => Math.Clamp(Environment.ProcessorCount / 2, 1, MaximumThreadCount);

        public static WorkerRecognitionConfiguration Create(
            string vadModelPath,
            string whisperModelPath,
            string language = "auto",
            int? threadCount = null)
        {
            var vad = ValidateAbsoluteFile(vadModelPath, nameof(vadModelPath));
            var whisper = ValidateAbsoluteFile(whisperModelPath, nameof(whisperModelPath));
            var normalizedLanguage = ValidateLanguage(language);
            var threads = threadCount ?? DefaultThreadCount;
            if (threads is < 1 or > MaximumThreadCount)
                throw new ArgumentOutOfRangeException(nameof(threadCount), threads,
                    $"Recognition thread count must be between 1 and {MaximumThreadCount}.");
            return new(true, vad, whisper, normalizedLanguage, threads);
        }

        private static string ValidateAbsoluteFile(string path, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A model path is required.", parameterName);
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Model paths must be absolute.", parameterName);
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("The configured model file does not exist.", fullPath);
            return fullPath;
        }

        private static string ValidateLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) throw new ArgumentException("Recognition language is required.", nameof(language));
            var value = language.Trim().ToLowerInvariant();
            if (value == "auto") return value;
            if (value.Length is < 2 or > 12 || !value.All(character => character is >= 'a' and <= 'z' or '-'))
                throw new ArgumentException("Recognition language must be 'auto' or a lowercase language code.", nameof(language));
            return value;
        }
    }
}
