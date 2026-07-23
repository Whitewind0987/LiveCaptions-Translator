namespace LiveCaptionsTranslator.tools.asrworkerprobe;

internal sealed record CanceledAudioSessionFacts(
    bool CaptureStopped,
    bool PipelineStopped,
    bool PipelineHasFailure,
    bool PumpCompleted,
    bool PumpJoined,
    bool SourceCompletionObserved,
    bool OwnedCancellationUsed,
    long FramesProduced,
    long FramesConsumed,
    long FramesDropped,
    long PumpFrames,
    long TransportFrames,
    long WorkerSummaryFrames,
    long PumpBytes,
    long TransportBytes,
    long WorkerSummaryBytes,
    long PumpSourceGaps,
    long WorkerSummaryGaps,
    long WorkerInvalidFrames,
    long HeartbeatFailures,
    bool GracefulShutdown,
    bool ForcedTermination,
    int? WorkerExitCode,
    bool HasCleanupFailures,
    bool HasDisposalFailures,
    bool HasRemainingWorkerPid);

internal static class ProbeAcceptance
{
    public static bool IsCleanCanceledAudioSession(CanceledAudioSessionFacts value) =>
        value.CaptureStopped &&
        value.PipelineStopped &&
        !value.PipelineHasFailure &&
        value.PumpCompleted &&
        value.PumpJoined &&
        value.SourceCompletionObserved &&
        !value.OwnedCancellationUsed &&
        value.FramesProduced == value.FramesConsumed + value.FramesDropped &&
        value.FramesConsumed == value.PumpFrames &&
        value.PumpFrames == value.TransportFrames &&
        value.TransportFrames == value.WorkerSummaryFrames &&
        value.PumpBytes == value.TransportBytes &&
        value.TransportBytes == value.WorkerSummaryBytes &&
        value.PumpSourceGaps == value.FramesDropped &&
        value.WorkerSummaryGaps == value.PumpSourceGaps &&
        value.WorkerInvalidFrames == 0 &&
        value.HeartbeatFailures == 0 &&
        value.GracefulShutdown &&
        !value.ForcedTermination &&
        value.WorkerExitCode == 0 &&
        !value.HasCleanupFailures &&
        !value.HasDisposalFailures &&
        !value.HasRemainingWorkerPid;

    public static bool IsValidExplicitRestart(
        Guid firstSessionId,
        int firstWorkerPid,
        bool firstWorkerExited,
        bool firstSessionAccepted,
        Guid secondSessionId,
        int secondWorkerPid,
        bool secondSessionAccepted) =>
        firstSessionId != Guid.Empty &&
        secondSessionId != Guid.Empty &&
        firstSessionId != secondSessionId &&
        firstWorkerPid > 0 &&
        secondWorkerPid > 0 &&
        firstWorkerExited &&
        firstSessionAccepted &&
        secondSessionAccepted;
}
