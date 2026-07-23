using LiveCaptionsTranslator.lifecycle;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class ApplicationShutdownCoordinatorTests
{
    [Fact]
    public async Task FaultedLoopDoesNotPreventSourceStopOrDisposal()
    {
        var stopCount = 0;
        var disposeCount = 0;
        var loopFailure = new InvalidOperationException("loop failed");

        var failures = await ApplicationShutdownCoordinator.RunAsync(
            () => { },
            [Task.FromException(loopFailure)],
            () =>
            {
                stopCount++;
                return Task.CompletedTask;
            },
            () =>
            {
                disposeCount++;
                return ValueTask.CompletedTask;
            });

        var failure = Assert.Single(failures);
        Assert.Equal(ApplicationShutdownPhase.ObserveBackgroundLoop, failure.Phase);
        Assert.Same(loopFailure, failure.Exception);
        Assert.Equal(1, stopCount);
        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public async Task EveryShutdownPhaseIsAttemptedAndAllFailuresAreRetained()
    {
        var stopAttempted = false;
        var disposeAttempted = false;

        var failures = await ApplicationShutdownCoordinator.RunAsync(
            () => throw new InvalidOperationException("cancel failed"),
            [Task.FromException(new InvalidOperationException("loop failed"))],
            () =>
            {
                stopAttempted = true;
                throw new InvalidOperationException("stop failed");
            },
            () =>
            {
                disposeAttempted = true;
                throw new InvalidOperationException("dispose failed");
            });

        Assert.True(stopAttempted);
        Assert.True(disposeAttempted);
        Assert.Equal(
            [
                ApplicationShutdownPhase.CancelBackgroundLoops,
                ApplicationShutdownPhase.ObserveBackgroundLoop,
                ApplicationShutdownPhase.StopCaptionSource,
                ApplicationShutdownPhase.DisposeCaptionSource
            ],
            failures.Select(failure => failure.Phase));
    }
}
