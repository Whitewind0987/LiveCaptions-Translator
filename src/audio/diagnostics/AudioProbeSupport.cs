using System.Buffers.Binary;
using System.IO;

namespace LiveCaptionsTranslator.audio.diagnostics
{
    public readonly record struct AudioLevelStatistics(double Rms, double Peak);

    public interface IAudioProbeWaveWriter : IAsyncDisposable
    {
        Task WriteAsync(
            NormalizedAudioFrame frame,
            CancellationToken cancellationToken = default);
    }

    public sealed record AudioProbeRunResult(
        int ExitCode,
        AudioCaptureStartResult? StartResult,
        AudioCaptureDiagnostics? StartedDiagnostics,
        AudioCaptureDiagnostics FinalDiagnostics,
        AudioLevelStatistics Levels,
        string? FailureReason);

    public sealed class AudioLevelAccumulator
    {
        private double sumSquares;
        private long sampleCount;
        private int peak;

        public void AddFrame(NormalizedAudioFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            var bytes = frame.Payload.AsSpan();
            for (var offset = 0; offset < bytes.Length; offset += 2)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset, 2));
                var normalized = sample / 32768d;
                sumSquares += normalized * normalized;
                sampleCount++;
                peak = Math.Max(peak, Math.Abs((int)sample));
            }
        }

        public AudioLevelStatistics Snapshot() => new(
            sampleCount == 0 ? 0 : Math.Sqrt(sumSquares / sampleCount),
            peak / 32768d);
    }

    public static class AudioProbeDuration
    {
        public static CancellationTokenSource Create(
            TimeSpan duration,
            CancellationToken externalCancellation = default)
        {
            if (duration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
            cancellation.CancelAfter(duration);
            return cancellation;
        }
    }

    public sealed class NormalizedWaveFileWriter : IAudioProbeWaveWriter
    {
        private readonly FileStream stream;
        private long dataLength;
        private int disposed;

        public NormalizedWaveFileWriter(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A WAV output path is required.", nameof(path));
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.Write(new byte[44]);
        }

        public async Task WriteAsync(
            NormalizedAudioFrame frame,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            await stream.WriteAsync(frame.Payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            dataLength += frame.Payload.Length;
        }

        public static byte[] CreateHeader(int dataLength)
        {
            if (dataLength < 0)
                throw new ArgumentOutOfRangeException(nameof(dataLength));
            var header = new byte[44];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 36 + dataLength);
            "WAVE"u8.CopyTo(header.AsSpan(8));
            "fmt "u8.CopyTo(header.AsSpan(12));
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), NormalizedAudioFormat.Channels);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), NormalizedAudioFormat.SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(
                header.AsSpan(28, 4),
                NormalizedAudioFormat.SampleRate * NormalizedAudioFormat.BytesPerSample);
            BinaryPrimitives.WriteInt16LittleEndian(
                header.AsSpan(32, 2),
                NormalizedAudioFormat.Channels * NormalizedAudioFormat.BytesPerSample);
            BinaryPrimitives.WriteInt16LittleEndian(
                header.AsSpan(34, 2),
                NormalizedAudioFormat.BitsPerSample);
            "data"u8.CopyTo(header.AsSpan(36));
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), dataLength);
            return header;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try
            {
                stream.Position = 0;
                var header = CreateHeader(checked((int)dataLength));
                await stream.WriteAsync(header).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static class AudioProbeRunner
    {
        public static Task<AudioProbeRunResult> RunAsync(
            AudioCaptureService service,
            string? savedEndpointId,
            TimeSpan duration,
            IAudioProbeWaveWriter? writer = null,
            CancellationToken cancellationToken = default) =>
            RunAsync(service, savedEndpointId, duration, writer, cancellationToken, null);

        internal static async Task<AudioProbeRunResult> RunAsync(
            AudioCaptureService service,
            string? savedEndpointId,
            TimeSpan duration,
            IAudioProbeWaveWriter? writer,
            CancellationToken cancellationToken,
            Func<AudioCaptureService, Task>? afterStart)
        {
            ArgumentNullException.ThrowIfNull(service);
            using var durationCancellation = AudioProbeDuration.Create(duration, cancellationToken);
            var levels = new AudioLevelAccumulator();
            AudioCaptureStartResult? startResult = null;
            AudioCaptureDiagnostics? startedDiagnostics = null;
            string? runFailure = null;
            try
            {
                startResult = await service.StartAsync(savedEndpointId, durationCancellation.Token)
                    .ConfigureAwait(false);
                if (!startResult.Success)
                {
                    runFailure = startResult.FailureReason ?? "Audio capture failed to start.";
                }
                else
                {
                    startedDiagnostics = service.Diagnostics;
                    if (afterStart != null)
                        await afterStart(service).ConfigureAwait(false);
                    while (!durationCancellation.IsCancellationRequested)
                    {
                        NormalizedAudioFrame frame;
                        try
                        {
                            frame = await service.FrameBuffer.ReadAsync(durationCancellation.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (durationCancellation.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (System.Threading.Channels.ChannelClosedException)
                        {
                            runFailure = service.FailureReason ??
                                "Audio frame buffer closed before the requested capture completed.";
                            break;
                        }

                        levels.AddFrame(frame);
                        if (writer != null)
                            await writer.WriteAsync(frame, durationCancellation.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (durationCancellation.IsCancellationRequested)
            {
                // Requested duration or external cancellation is a clean termination.
            }
            catch (Exception ex)
            {
                runFailure = $"Audio probe failed: {ex.Message}";
            }
            finally
            {
                if (writer != null)
                {
                    try { await writer.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        runFailure = Combine(runFailure, $"WAV finalization failed: {ex.Message}");
                    }
                }

                try { await service.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    runFailure = Combine(runFailure, $"Capture cleanup failed: {ex.Message}");
                }
            }

            var finalDiagnostics = service.Diagnostics;
            if (finalDiagnostics.State is AudioCaptureState.Unavailable or AudioCaptureState.Faulted)
                runFailure = Combine(runFailure, finalDiagnostics.LastFailureReason);
            else if (!string.IsNullOrWhiteSpace(finalDiagnostics.LastFailureReason))
                runFailure = Combine(runFailure, finalDiagnostics.LastFailureReason);

            return new AudioProbeRunResult(
                string.IsNullOrWhiteSpace(runFailure) ? 0 : 1,
                startResult,
                startedDiagnostics,
                finalDiagnostics,
                levels.Snapshot(),
                runFailure);
        }

        private static string? Combine(string? first, string? second)
        {
            if (string.IsNullOrWhiteSpace(first))
                return second;
            if (string.IsNullOrWhiteSpace(second) || first.Contains(second, StringComparison.Ordinal))
                return first;
            return $"{first} {second}";
        }
    }
}
