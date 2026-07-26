using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.utils
{
    internal sealed class Stage65AcceptanceValueTracker<T>
    {
        private readonly object gate = new();
        private bool hasValue;
        private T value = default!;

        internal bool TryObserve(T next)
        {
            lock (gate)
            {
                if (hasValue && EqualityComparer<T>.Default.Equals(value, next))
                    return false;

                value = next;
                hasValue = true;
                return true;
            }
        }
    }

    internal static class Stage65AcceptanceTrace
    {
        internal const string EvidencePathEnvironmentVariable =
            "LCT_STAGE65_EVIDENCE_PATH";

        private static readonly object WriteLock = new();
        internal static bool IsEnabled => GetEvidencePath() != null;

        internal static void Write(string eventName, object? data = null)
        {
            var path = GetEvidencePath();
            if (path == null)
                return;

            try
            {
                var line = JsonSerializer.Serialize(new
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Event = eventName,
                    Data = data
                });
                lock (WriteLock)
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Stage 6.5 acceptance trace failed: {ex}");
            }
        }

        private static string? GetEvidencePath()
        {
            var path = Environment.GetEnvironmentVariable(
                EvidencePathEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                ? path
                : null;
        }
    }
}
