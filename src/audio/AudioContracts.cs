using System.Collections.Immutable;

namespace LiveCaptionsTranslator.audio
{
    public enum AudioEndpointAvailability
    {
        Active,
        Disabled,
        Unplugged,
        Unknown
    }

    public sealed record AudioEndpointInfo(
        string Id,
        string DisplayName,
        bool IsDefault,
        AudioEndpointAvailability Availability)
    {
        public bool IsActive => Availability == AudioEndpointAvailability.Active;
    }

    public sealed record AudioEndpointEnumerationResult(
        bool Success,
        IReadOnlyList<AudioEndpointInfo> Endpoints,
        string? FailureReason)
    {
        public static AudioEndpointEnumerationResult Available(IReadOnlyList<AudioEndpointInfo> endpoints) =>
            new(true, endpoints, null);

        public static AudioEndpointEnumerationResult Unavailable(string reason) =>
            new(false, [], reason);
    }

    public sealed record AudioEndpointResolution(
        bool Success,
        AudioEndpointInfo? Endpoint,
        bool UsedFallback,
        string? Diagnostic,
        string? FailureReason)
    {
        public static AudioEndpointResolution Resolved(
            AudioEndpointInfo endpoint,
            bool usedFallback = false,
            string? diagnostic = null) =>
            new(true, endpoint, usedFallback, diagnostic, null);

        public static AudioEndpointResolution Unavailable(string reason) =>
            new(false, null, false, null, reason);
    }

    public interface IAudioEndpointProvider : IAsyncDisposable
    {
        Task<AudioEndpointEnumerationResult> EnumerateAsync(
            CancellationToken cancellationToken = default);

        Task<AudioEndpointResolution> ResolveAsync(
            string? savedEndpointId,
            CancellationToken cancellationToken = default);
    }

    public static class AudioEndpointResolver
    {
        public static AudioEndpointResolution Resolve(
            IEnumerable<AudioEndpointInfo> endpoints,
            string? savedEndpointId)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            var available = endpoints.Where(endpoint => endpoint.IsActive).ToArray();
            var activeDefault = available.FirstOrDefault(endpoint => endpoint.IsDefault);
            if (string.IsNullOrWhiteSpace(savedEndpointId))
            {
                return activeDefault == null
                    ? AudioEndpointResolution.Unavailable("No active default render endpoint is available.")
                    : AudioEndpointResolution.Resolved(activeDefault);
            }

            var normalizedId = savedEndpointId.Trim();
            var saved = available.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Id, normalizedId, StringComparison.Ordinal));
            if (saved != null)
                return AudioEndpointResolution.Resolved(saved);
            if (activeDefault == null)
            {
                return AudioEndpointResolution.Unavailable(
                    $"Saved render endpoint '{normalizedId}' is unavailable and no active default exists.");
            }

            return AudioEndpointResolution.Resolved(
                activeDefault,
                usedFallback: true,
                $"Saved render endpoint '{normalizedId}' is unavailable; using the current system default.");
        }
    }

    public enum AudioSampleEncoding
    {
        IeeeFloat,
        PcmSignedInteger
    }

    public sealed record AudioInputFormat
    {
        public int SampleRate { get; }
        public int Channels { get; }
        public int BitsPerSample { get; }
        public int BlockAlign { get; }
        public AudioSampleEncoding Encoding { get; }

        public AudioInputFormat(
            int sampleRate,
            int channels,
            int bitsPerSample,
            int blockAlign,
            AudioSampleEncoding encoding)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels));
            if (bitsPerSample <= 0 || bitsPerSample % 8 != 0)
                throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
            if (blockAlign != channels * (bitsPerSample / 8))
                throw new ArgumentOutOfRangeException(nameof(blockAlign));
            if (!Enum.IsDefined(encoding))
                throw new ArgumentOutOfRangeException(nameof(encoding));

            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
            BlockAlign = blockAlign;
            Encoding = encoding;
        }

        public override string ToString() =>
            $"{SampleRate} Hz, {Channels} channel(s), {Encoding}, {BitsPerSample}-bit";
    }

    public static class NormalizedAudioFormat
    {
        public const int SampleRate = 16000;
        public const int Channels = 1;
        public const int BitsPerSample = 16;
        public const int FrameDurationMilliseconds = 20;
        public const int SamplesPerFrame = 320;
        public const int BytesPerSample = 2;
        public const int BytesPerFrame = 640;

        public static string Description =>
            "16000 Hz, mono, signed PCM16 little-endian, 20 ms frames";
    }

    public sealed class NormalizedAudioFrame
    {
        public Guid SessionId { get; }
        public long Sequence { get; }
        public long StartSampleIndex { get; }
        public DateTimeOffset CapturedAtUtc { get; }
        public ImmutableArray<byte> Payload { get; }

        public NormalizedAudioFrame(
            Guid sessionId,
            long sequence,
            long startSampleIndex,
            DateTimeOffset capturedAtUtc,
            ReadOnlySpan<byte> payload)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentException("Audio session identity must not be empty.", nameof(sessionId));
            if (sequence < 1)
                throw new ArgumentOutOfRangeException(nameof(sequence));
            if (startSampleIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(startSampleIndex));
            if (capturedAtUtc == default || capturedAtUtc.Offset != TimeSpan.Zero)
                throw new ArgumentException("Capture timestamp must be non-default UTC.", nameof(capturedAtUtc));
            if (payload.Length != NormalizedAudioFormat.BytesPerFrame)
            {
                throw new ArgumentException(
                    $"Audio frames must contain exactly {NormalizedAudioFormat.BytesPerFrame} bytes.",
                    nameof(payload));
            }

            SessionId = sessionId;
            Sequence = sequence;
            StartSampleIndex = startSampleIndex;
            CapturedAtUtc = capturedAtUtc;
            Payload = ImmutableArray.Create(payload.ToArray());
        }
    }

    public enum AudioCaptureState
    {
        Stopped,
        Starting,
        Running,
        Unavailable,
        Faulted,
        Stopping
    }

    public sealed record AudioCaptureStatus(
        AudioCaptureState State,
        string? FailureReason,
        DateTimeOffset ChangedAtUtc);

    public sealed record AudioCaptureStartResult(
        bool Success,
        AudioCaptureState State,
        Guid? SessionId,
        AudioEndpointInfo? Endpoint,
        bool UsedEndpointFallback,
        string? Diagnostic,
        string? FailureReason)
    {
        public static AudioCaptureStartResult Started(
            Guid sessionId,
            AudioEndpointInfo endpoint,
            bool usedFallback,
            string? diagnostic) =>
            new(true, AudioCaptureState.Running, sessionId, endpoint, usedFallback, diagnostic, null);

        public static AudioCaptureStartResult Failed(
            AudioCaptureState state,
            string reason) =>
            new(false, state, null, null, false, null, reason);
    }

    public sealed record AudioCaptureDiagnostics(
        Guid? SessionId,
        AudioCaptureState State,
        string? RequestedEndpointId,
        string? ResolvedEndpointId,
        string? ResolvedEndpointName,
        bool UsedEndpointFallback,
        AudioInputFormat? InputFormat,
        string NormalizedFormat,
        long FramesProduced,
        long FramesConsumed,
        int FramesBuffered,
        long FramesDropped,
        long InputBytesReceived,
        long NormalizedBytesProduced,
        long LastFrameSequence,
        string? LastFailureReason,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? LastFrameAtUtc);

    public sealed record AudioCaptureData(ReadOnlyMemory<byte> Bytes, DateTimeOffset CapturedAtUtc);

    public enum AudioRuntimeStopReason
    {
        Expected,
        Unavailable,
        Faulted
    }

    public sealed record AudioRuntimeStopped(
        AudioRuntimeStopReason Reason,
        string? FailureReason);

    public sealed record AudioRuntimeOpenResult(
        bool Success,
        AudioInputFormat? InputFormat,
        AudioCaptureState FailureState,
        string? FailureReason)
    {
        public static AudioRuntimeOpenResult Opened(AudioInputFormat inputFormat) =>
            new(true, inputFormat, AudioCaptureState.Starting, null);

        public static AudioRuntimeOpenResult Failed(AudioCaptureState state, string reason) =>
            new(false, null, state, reason);
    }

    public interface IAudioCaptureRuntime : IAsyncDisposable
    {
        event EventHandler<AudioCaptureData>? DataAvailable;
        event EventHandler<AudioRuntimeStopped>? RecordingStopped;

        Task<AudioRuntimeOpenResult> OpenAsync(
            AudioEndpointInfo endpoint,
            CancellationToken cancellationToken = default);
        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
    }

    public interface IAudioCaptureRuntimeFactory
    {
        IAudioCaptureRuntime Create();
    }
}
