using LiveCaptionsTranslator.tools.asrworkerprobe;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class AsrWorkerProbeOptionsTests
{
    [Fact]
    public void AudioRestartIsAcceptedAndSelected()
    {
        var options = Parse("--audio", "--restart");
        Assert.True(options.Audio);
        Assert.True(options.Restart);
        Assert.False(options.ControlledExit);
    }

    [Fact]
    public void AudioControlledExitIsAcceptedAndSelected()
    {
        var options = Parse("--audio", "--controlled-exit");
        Assert.True(options.Audio);
        Assert.True(options.ControlledExit);
        Assert.False(options.Restart);
    }

    [Fact]
    public void RestartAndControlledExitTogetherAreRejected()
    {
        var error = Assert.Throws<ArgumentException>(() => Parse("--audio", "--restart", "--controlled-exit"));
        Assert.Contains("cannot be used together", error.Message);
    }

    [Fact]
    public void SlowWorkerWithAudioIsRejected()
    {
        var error = Assert.Throws<ArgumentException>(() => Parse("--audio", "--slow-worker"));
        Assert.Contains("requires --synthetic", error.Message);
    }

    [Fact]
    public void DeviceWithSyntheticIsRejectedInsteadOfIgnored()
    {
        var error = Assert.Throws<ArgumentException>(() => Parse("--synthetic", "--device", "default"));
        Assert.Contains("requires --audio", error.Message);
    }

    [Fact]
    public void SyntheticSlowWorkerRemainsAccepted()
    {
        var options = Parse("--synthetic", "--slow-worker");
        Assert.True(options.Synthetic);
        Assert.True(options.SlowWorker);
    }

    [Fact]
    public void MissingOptionValueHasExplicitDiagnostic()
    {
        var error = Assert.Throws<ArgumentException>(() => ProbeOptionsParser.Parse(["--worker"]));
        Assert.Equal("--worker requires a value.", error.Message);
    }

    private static ProbeOptions Parse(params string[] modeArguments) =>
        ProbeOptionsParser.Parse(["--worker", "worker.exe", "--duration", "10", .. modeArguments]);
}
