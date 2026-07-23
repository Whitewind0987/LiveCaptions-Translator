using System.Buffers.Binary;

using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.diagnostics;
using Xunit;

#pragma warning disable xUnit1051 // Probe helpers use explicit bounded cancellation in these tests.

namespace LiveCaptionsTranslator.Tests;

public sealed class AudioProbeSupportTests
{
    [Fact]
    public void RmsAndPeakAreCalculatedFromNormalizedPcm()
    {
        var accumulator = new AudioLevelAccumulator();
        accumulator.AddFrame(Frame(AudioTestData.Pcm16(
            Enumerable.Repeat((short)16384, 320).ToArray())));
        var result = accumulator.Snapshot();
        Assert.InRange(result.Rms, 0.4999, 0.5001);
        Assert.InRange(result.Peak, 0.4999, 0.5001);
    }

    [Fact]
    public void WavHeaderContainsExactNormalizedFormat()
    {
        var header = NormalizedWaveFileWriter.CreateHeader(640);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(header, 8, 4));
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(22, 2)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(34, 2)));
        Assert.Equal(640, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40, 4)));
    }

    [Fact]
    public async Task DurationCancellationIsBounded()
    {
        using var cancellation = AudioProbeDuration.Create(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token));
    }

    [Fact]
    public async Task WavFileFinalizesHeaderAfterCancellationStyleDisposal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lct-probe-{Guid.NewGuid():N}.wav");
        try
        {
            await using (var writer = new NormalizedWaveFileWriter(path))
                await writer.WriteAsync(Frame(new byte[640]));
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(684, bytes.Length);
            Assert.Equal(640, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(AudioRuntimeStopReason.Unavailable, AudioCaptureState.Unavailable)]
    [InlineData(AudioRuntimeStopReason.Faulted, AudioCaptureState.Faulted)]
    public async Task ProbeReturnsFailureForUnexpectedBufferCompletion(
        AudioRuntimeStopReason stopReason,
        AudioCaptureState expectedState)
    {
        var runtime = new FakeAudioCaptureRuntime();
        await using var service = CreateService(runtime);
        var result = await AudioProbeRunner.RunAsync(
            service,
            null,
            TimeSpan.FromSeconds(10),
            null,
            CancellationToken.None,
            _ =>
            {
                runtime.EmitStopped(stopReason, "capture failed");
                return Task.CompletedTask;
            });

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(expectedState, result.FinalDiagnostics.State);
        Assert.Contains("capture failed", result.FailureReason);
    }

    [Fact]
    public async Task ProbeReturnsSuccessForRequestedCancellation()
    {
        var runtime = new FakeAudioCaptureRuntime();
        await using var service = CreateService(runtime);
        using var cancellation = new CancellationTokenSource();
        var result = await AudioProbeRunner.RunAsync(
            service,
            null,
            TimeSpan.FromSeconds(10),
            null,
            cancellation.Token,
            _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AudioCaptureState.Stopped, result.FinalDiagnostics.State);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task ProbeFinalizesWavOnUnexpectedFailure()
    {
        var runtime = new FakeAudioCaptureRuntime();
        await using var service = CreateService(runtime);
        var writer = new FakeProbeWaveWriter();
        var result = await AudioProbeRunner.RunAsync(
            service,
            null,
            TimeSpan.FromSeconds(10),
            writer,
            CancellationToken.None,
            _ =>
            {
                runtime.EmitStopped(AudioRuntimeStopReason.Faulted, "capture failed");
                return Task.CompletedTask;
            });

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, writer.DisposeCount);
    }

    [Fact]
    public async Task ProbeSnapshotsDiagnosticsAfterCleanup()
    {
        var runtime = new FakeAudioCaptureRuntime
        {
            StopException = new InvalidOperationException("cleanup marker")
        };
        await using var service = CreateService(runtime);
        using var cancellation = new CancellationTokenSource();
        var result = await AudioProbeRunner.RunAsync(
            service,
            null,
            TimeSpan.FromSeconds(10),
            null,
            cancellation.Token,
            _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            });

        Assert.Equal(AudioCaptureState.Stopped, result.FinalDiagnostics.State);
        Assert.Contains("cleanup marker", result.FinalDiagnostics.LastFailureReason);
        Assert.Equal(1, result.ExitCode);
    }

    private static NormalizedAudioFrame Frame(byte[] payload) => new(
        new Guid("44444444-4444-4444-4444-444444444444"),
        1,
        0,
        new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero),
        payload);

    private static AudioCaptureService CreateService(FakeAudioCaptureRuntime runtime)
    {
        var factory = new FakeAudioCaptureRuntimeFactory();
        factory.Enqueue(runtime);
        return new AudioCaptureService(new FakeAudioEndpointProvider(), factory);
    }

    private sealed class FakeProbeWaveWriter : IAudioProbeWaveWriter
    {
        internal int DisposeCount { get; private set; }
        public Task WriteAsync(
            NormalizedAudioFrame frame,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
