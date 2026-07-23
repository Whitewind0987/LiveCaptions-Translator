using LiveCaptionsTranslator.tools.asrworkerprobe;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class AsrWorkerProbeAcceptanceTests
{
    [Fact]
    public void CancellationWithZeroDropsPasses() =>
        Assert.True(ProbeAcceptance.IsCleanCanceledAudioSession(Canceled()));

    [Fact]
    public void CancellationWithConsistentDropsAndGapsPasses() =>
        Assert.True(ProbeAcceptance.IsCleanCanceledAudioSession(Canceled() with
        {
            FramesProduced = 651,
            FramesConsumed = 623,
            FramesDropped = 28,
            PumpFrames = 623,
            TransportFrames = 623,
            WorkerSummaryFrames = 623,
            PumpSourceGaps = 28,
            WorkerSummaryGaps = 28
        }));

    [Fact]
    public void CancellationWithInconsistentFrameTotalsFails() =>
        Assert.False(ProbeAcceptance.IsCleanCanceledAudioSession(Canceled() with { FramesProduced = 101 }));

    [Fact]
    public void CancellationWithInconsistentGapTotalsFails() =>
        Assert.False(ProbeAcceptance.IsCleanCanceledAudioSession(Canceled() with { WorkerSummaryGaps = 1 }));

    [Fact]
    public void CancellationWithCleanupFailureFails() =>
        Assert.False(ProbeAcceptance.IsCleanCanceledAudioSession(Canceled() with { HasCleanupFailures = true }));

    [Fact]
    public void CancellationWithDisposalFailureFails() =>
        Assert.False(ProbeAcceptance.IsCleanCanceledAudioSession(Canceled() with { HasDisposalFailures = true }));

    [Fact]
    public void CancellationWithForcedTerminationFails() =>
        Assert.False(ProbeAcceptance.IsCleanCanceledAudioSession(Canceled() with { ForcedTermination = true }));

    [Fact]
    public void CancellationWithRemainingPidFails() =>
        Assert.False(ProbeAcceptance.IsCleanCanceledAudioSession(Canceled() with { HasRemainingWorkerPid = true }));

    [Fact]
    public void ExplicitRestartAllowsPidReuseAfterFirstWorkerExit()
    {
        Assert.True(ProbeAcceptance.IsValidExplicitRestart(
            Guid.NewGuid(), 42, true, true, Guid.NewGuid(), 42, true));
    }

    [Fact]
    public void ExplicitRestartRequiresFirstWorkerToExit()
    {
        Assert.False(ProbeAcceptance.IsValidExplicitRestart(
            Guid.NewGuid(), 42, false, true, Guid.NewGuid(), 43, true));
    }

    private static CanceledAudioSessionFacts Canceled() => new(
        CaptureStopped: true,
        PipelineStopped: true,
        PipelineHasFailure: false,
        PumpCompleted: true,
        PumpJoined: true,
        SourceCompletionObserved: true,
        OwnedCancellationUsed: false,
        FramesProduced: 100,
        FramesConsumed: 100,
        FramesDropped: 0,
        PumpFrames: 100,
        TransportFrames: 100,
        WorkerSummaryFrames: 100,
        PumpBytes: 64_000,
        TransportBytes: 64_000,
        WorkerSummaryBytes: 64_000,
        PumpSourceGaps: 0,
        WorkerSummaryGaps: 0,
        WorkerInvalidFrames: 0,
        HeartbeatFailures: 0,
        GracefulShutdown: true,
        ForcedTermination: false,
        WorkerExitCode: 0,
        HasCleanupFailures: false,
        HasDisposalFailures: false,
        HasRemainingWorkerPid: false);
}
