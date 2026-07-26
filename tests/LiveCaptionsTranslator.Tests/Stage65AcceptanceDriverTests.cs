using LiveCaptionsTranslator.utils;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class Stage65AcceptanceDriverTests
{
    [Fact]
    public void DriverIsDisabledByDefault()
    {
        Assert.False(Stage65AcceptanceDriver.TryLoadConfiguration(
            null, null, out _, out var failure));
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("relative.json", "C:\\evidence.jsonl")]
    [InlineData("C:\\plan.json", "relative.jsonl")]
    [InlineData(null, "C:\\evidence.jsonl")]
    [InlineData("C:\\plan.json", null)]
    public void RelativeOrMissingPathsAreRejected(
        string? planPath,
        string? evidencePath)
    {
        Assert.False(Stage65AcceptanceDriver.TryLoadConfiguration(
            planPath, evidencePath, out _, out var failure));
        Assert.NotNull(failure);
    }

    [Fact]
    public void AllowedActionsAreParsedWithBoundedTimeouts()
    {
        const string json = """
            {
              "sessionId": "session-a",
              "actions": [
                { "name": "open-overlay", "timeoutSeconds": 10 },
                { "name": "wait-display-contains", "value": "local", "timeoutSeconds": 120 },
                { "name": "enable-log-only" },
                { "name": "open-settings" },
                { "name": "select-windows" },
                { "name": "select-local" },
                { "name": "wait-source-failed" },
                { "name": "close-main-window" }
              ]
            }
            """;

        Assert.True(Stage65AcceptanceDriver.TryParsePlan(
            json, out var plan, out var failure), failure);
        Assert.Equal("session-a", plan.SessionId);
        Assert.Equal(8, plan.Actions.Count);
        Assert.Equal(60, plan.Actions[2].TimeoutSeconds);
        Assert.Equal("local", plan.Actions[1].Value);
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData("wait-display-contains", null)]
    public void UnsupportedOrIncompleteActionsAreRejected(
        string name,
        string? value)
    {
        var valueJson = value == null ? string.Empty : $", \"value\": \"{value}\"";
        var json = $$"""
            {
              "sessionId": "session-a",
              "actions": [ { "name": "{{name}}"{{valueJson}} } ]
            }
            """;

        Assert.False(Stage65AcceptanceDriver.TryParsePlan(
            json, out _, out var failure));
        Assert.NotNull(failure);
    }

    [Fact]
    public void ExplicitAbsolutePathsLoadWithoutExecutingProductionActions()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"lct-stage65-driver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var planPath = Path.Combine(directory, "plan.json");
        var evidencePath = Path.Combine(directory, "evidence.jsonl");
        File.WriteAllText(planPath,
            """{"sessionId":"session-a","actions":[{"name":"open-overlay"}]}""");
        try
        {
            Assert.True(Stage65AcceptanceDriver.TryLoadConfiguration(
                planPath, evidencePath, out var configuration, out var failure), failure);
            Assert.Equal(Path.GetFullPath(planPath), configuration.PlanPath);
            Assert.Equal(Path.GetFullPath(evidencePath), configuration.EvidencePath);
            Assert.False(File.Exists(evidencePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SessionLifetimeObservesExternalCancellationAndDisposesIdempotently()
    {
        using var cancellation = new CancellationTokenSource();
        var lifetime = new Stage65AcceptanceSessionLifetime(cancellation.Token);
        var ticket = lifetime.Capture();
        Assert.True(lifetime.IsCurrent(ticket));

        cancellation.Cancel();

        Assert.True(lifetime.Token.IsCancellationRequested);
        Assert.False(lifetime.IsCurrent(ticket));
        lifetime.Dispose();
        lifetime.Dispose();
    }

    [Fact]
    public void DisposedSessionRejectsStaleCompletionFromEarlierApplicationRun()
    {
        var first = new Stage65AcceptanceSessionLifetime();
        var staleTicket = first.Capture();
        first.Dispose();
        using var second = new Stage65AcceptanceSessionLifetime();
        var currentTicket = second.Capture();

        Assert.False(first.IsCurrent(staleTicket));
        Assert.True(second.IsCurrent(currentTicket));
    }
}
