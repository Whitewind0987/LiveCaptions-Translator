using System.Text.Json;
using LiveCaptionsTranslator.utils;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class Stage65AcceptanceTraceTests
{
    [Fact]
    public void ValueTrackerRecordsInitialAndChangedValuesOnly()
    {
        var tracker = new Stage65AcceptanceValueTracker<string>();

        Assert.True(tracker.TryObserve("first"));
        Assert.False(tracker.TryObserve("first"));
        Assert.True(tracker.TryObserve("second"));
        Assert.False(tracker.TryObserve("second"));
    }

    [Fact]
    public void TraceIsDormantUnlessAnAbsoluteEvidencePathIsExplicitlyConfigured()
    {
        var original = Environment.GetEnvironmentVariable(
            Stage65AcceptanceTrace.EvidencePathEnvironmentVariable);
        var directory = Path.Combine(Path.GetTempPath(), $"lct-stage65-trace-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "trace.jsonl");
        Directory.CreateDirectory(directory);

        try
        {
            Environment.SetEnvironmentVariable(
                Stage65AcceptanceTrace.EvidencePathEnvironmentVariable, null);
            Assert.False(Stage65AcceptanceTrace.IsEnabled);
            Stage65AcceptanceTrace.Write("disabled", new { Value = 1 });
            Assert.False(File.Exists(path));

            Environment.SetEnvironmentVariable(
                Stage65AcceptanceTrace.EvidencePathEnvironmentVariable, "relative.jsonl");
            Assert.False(Stage65AcceptanceTrace.IsEnabled);
            Stage65AcceptanceTrace.Write("relative", new { Value = 2 });
            Assert.False(File.Exists(path));

            Environment.SetEnvironmentVariable(
                Stage65AcceptanceTrace.EvidencePathEnvironmentVariable, path);
            Assert.True(Stage65AcceptanceTrace.IsEnabled);
            Stage65AcceptanceTrace.Write("enabled", new { Value = 3 });

            var line = Assert.Single(File.ReadAllLines(path), value =>
            {
                using var candidate = JsonDocument.Parse(value);
                return candidate.RootElement.GetProperty("Event").GetString() == "enabled";
            });
            using var document = JsonDocument.Parse(line);
            Assert.Equal("enabled", document.RootElement.GetProperty("Event").GetString());
            Assert.Equal(3, document.RootElement.GetProperty("Data").GetProperty("Value").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Stage65AcceptanceTrace.EvidencePathEnvironmentVariable, original);
            Directory.Delete(directory, recursive: true);
        }
    }
}
