using LiveCaptionsTranslator.ipc;
using LiveCaptionsTranslator.captioning;

namespace LiveCaptionsTranslator.worker
{
    public enum AsrWorkerState { Stopped, Starting, Connecting, Handshaking, Ready, Streaming, Restarting, Unavailable, Faulted, Stopping }

    public enum AsrWorkerFailureKind
    {
        None,
        WorkerExecutableMissing,
        WorkerLaunchFailed,
        JobAssignmentFailed,
        ControlPipeTimeout,
        AudioPipeTimeout,
        HandshakeRejected,
        ProtocolMismatch,
        ProtocolViolation,
        WorkerReadyTimeout,
        HeartbeatTimeout,
        ControlPipeClosed,
        AudioPipeClosed,
        WorkerReportedError,
        InvalidRecognitionConfiguration,
        ModelLoadFailed,
        VadInferenceFailed,
        WhisperInferenceFailed,
        RecognitionDrainTimeout,
        WorkerExited,
        AudioCaptureFailed,
        AudioPumpFailed,
        GracefulShutdownTimeout,
        ForcedTerminationFailed,
        CleanupFailed
    }

    public sealed record AsrWorkerStatus(AsrWorkerState State, AsrWorkerFailureKind FailureKind, string? FailureReason, DateTimeOffset ChangedAtUtc);

    public sealed record WorkerTransportFailure(AsrWorkerFailureKind Kind, string Reason, Exception? Exception = null);

    public sealed class WorkerTransportException : Exception
    {
        public WorkerTransportException(AsrWorkerFailureKind kind, string message, Exception? innerException = null) : base(message, innerException) => Kind = kind;
        public AsrWorkerFailureKind Kind { get; }
    }

    public sealed record AsrWorkerStartResult(bool Success, AsrWorkerState State, Guid? SessionId, int? ProcessId, AsrWorkerFailureKind FailureKind, string? FailureReason)
    {
        public static AsrWorkerStartResult Started(Guid sessionId, int pid) => new(true, AsrWorkerState.Ready, sessionId, pid, AsrWorkerFailureKind.None, null);
        public static AsrWorkerStartResult Failed(AsrWorkerState state, AsrWorkerFailureKind kind, string reason) => new(false, state, null, null, kind, reason);
    }

    public sealed record AsrWorkerDiagnostics(
        AsrWorkerState State,
        Guid? WorkerSessionId,
        int? WorkerPid,
        string ExecutablePath,
        string? NegotiatedProtocol,
        WorkerCapabilities Capabilities,
        DateTimeOffset? HandshakeStartedAtUtc,
        DateTimeOffset? HandshakeCompletedAtUtc,
        DateTimeOffset? LatestPongAtUtc,
        long HeartbeatFailures,
        long ControlMessagesSent,
        long ControlMessagesReceived,
        long AudioFramesSent,
        long AudioBytesSent,
        long WorkerFramesReceived,
        long WorkerBytesReceived,
        long SequenceGaps,
        long WorkerInvalidFrames,
        Guid? ActiveCaptureSessionId,
        DateTimeOffset? LatestProgressAtUtc,
        AsrWorkerFailureKind TransportFailureKind,
        string? TransportFailureReason,
        int? ProcessExitCode,
        bool GracefulShutdownSucceeded,
        bool ForcedTerminationUsed,
        bool JobAssignmentSucceeded,
        IReadOnlyList<string> RecentStdout,
        IReadOnlyList<string> RecentStderr,
        AsrWorkerFailureKind FailureKind,
        string? FailureReason,
        IReadOnlyList<string> CleanupFailures);

    public sealed record WorkerLaunchRequest(string ExecutablePath, string ControlPipeName, string AudioPipeName, Guid SessionId, int ParentPid, byte[] AuthenticationNonce, WorkerRecognitionConfiguration? Recognition = null);

    public interface IWorkerProcess : IAsyncDisposable
    {
        int Id { get; }
        nint NativeHandle { get; }
        bool HasExited { get; }
        int? ExitCode { get; }
        Task Completion { get; }
        IReadOnlyList<string> RecentStdout { get; }
        IReadOnlyList<string> RecentStderr { get; }
        Task TerminateTreeAsync(CancellationToken cancellationToken = default);
    }

    public interface IWorkerProcessLauncher { Task<IWorkerProcess> LaunchAsync(WorkerLaunchRequest request, CancellationToken cancellationToken = default); }
    public interface IWorkerJob : IAsyncDisposable { bool AssignmentSucceeded { get; } string? FailureReason { get; } Task AssignAsync(IWorkerProcess process, CancellationToken cancellationToken = default); }
    public interface IWorkerJobFactory { IWorkerJob Create(); }

    public sealed record WorkerTransportStartResult(ushort NegotiatedMinor, WorkerCapabilities Capabilities, TimeSpan HandshakeLatency);

    public interface IAsrWorkerTransport : IAsyncDisposable
    {
        event EventHandler<AudioStreamSummaryPayload>? ProgressReceived;
        event EventHandler<ErrorPayload>? ErrorReceived;
        event EventHandler<CaptionEvent>? CaptionEventReceived { add { } remove { } }
        long ControlMessagesSent { get; }
        long ControlMessagesReceived { get; }
        long AudioFramesSent { get; }
        long AudioBytesSent { get; }
        DateTimeOffset? LatestPongAtUtc { get; }
        Task<WorkerTransportFailure> TerminalFailure { get; }
        Task<WorkerTransportStartResult> ConnectAndHandshakeAsync(int expectedPid, CancellationToken cancellationToken = default);
        Task StartAudioStreamAsync(StartAudioStreamPayload request, CancellationToken cancellationToken = default);
        Task SendAudioFrameAsync(Guid workerSessionId, audio.NormalizedAudioFrame frame, CancellationToken cancellationToken = default);
        Task EndAudioStreamAsync(AudioStreamEndPayload end, CancellationToken cancellationToken = default);
        Task<AudioStreamSummaryPayload> StopAudioStreamAsync(Guid workerSessionId, Guid captureSessionId, CancellationToken cancellationToken = default);
        Task PingAsync(CancellationToken cancellationToken = default);
        Task<bool> ShutdownAsync(Guid workerSessionId, CancellationToken cancellationToken = default);
        Task StopAsync();
    }

    public interface IAsrWorkerTransportFactory
    {
        IAsrWorkerTransport Create(ipc.WorkerPipeIdentity identity);
    }
}
