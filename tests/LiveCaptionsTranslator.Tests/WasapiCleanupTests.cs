using LiveCaptionsTranslator.audio.windows;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class WasapiCleanupTests
{
    [Fact]
    public void CaptureDisposalFailureDoesNotPreventDeviceDisposal()
    {
        var captureDisposeCount = 0;
        var deviceDisposeCount = 0;
        var result = NativeCleanupCoordinator.Run(
            new NativeCleanupStep("capture", () =>
            {
                captureDisposeCount++;
                throw new InvalidOperationException("capture dispose failed");
            }),
            new NativeCleanupStep("device", () => deviceDisposeCount++));

        Assert.Equal(1, captureDisposeCount);
        Assert.Equal(1, deviceDisposeCount);
        Assert.Contains("capture dispose failed", result.FailureReason);
    }
}
