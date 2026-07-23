using LiveCaptionsTranslator.audio;

namespace LiveCaptionsTranslator.Tests;

internal sealed class FakeAudioEndpointProvider : IAudioEndpointProvider
{
    internal static readonly AudioEndpointInfo DefaultEndpoint =
        new("default-id", "Default Speakers", true, AudioEndpointAvailability.Active);
    internal static readonly AudioEndpointInfo SavedEndpoint =
        new("saved-id", "Saved Speakers", false, AudioEndpointAvailability.Active);

    internal AudioEndpointEnumerationResult EnumerationResult { get; set; } =
        AudioEndpointEnumerationResult.Available([DefaultEndpoint, SavedEndpoint]);
    internal AudioEndpointResolution? ResolutionResult { get; set; }
    internal int EnumerateCount { get; private set; }
    internal int ResolveCount { get; private set; }
    internal int DisposeCount { get; private set; }

    public Task<AudioEndpointEnumerationResult> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnumerateCount++;
        return Task.FromResult(EnumerationResult);
    }

    public Task<AudioEndpointResolution> ResolveAsync(
        string? savedEndpointId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResolveCount++;
        return Task.FromResult(ResolutionResult ??
            AudioEndpointResolver.Resolve(EnumerationResult.Endpoints, savedEndpointId));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAudioCaptureRuntimeFactory : IAudioCaptureRuntimeFactory
{
    private readonly Queue<FakeAudioCaptureRuntime> queued = [];
    internal List<FakeAudioCaptureRuntime> Created { get; } = [];

    internal void Enqueue(FakeAudioCaptureRuntime runtime) => queued.Enqueue(runtime);

    public IAudioCaptureRuntime Create()
    {
        var runtime = queued.Count > 0 ? queued.Dequeue() : new FakeAudioCaptureRuntime();
        Created.Add(runtime);
        return runtime;
    }
}

internal sealed class FakeAudioCaptureRuntime : IAudioCaptureRuntime
{
    internal AudioRuntimeOpenResult OpenResult { get; set; } = AudioRuntimeOpenResult.Opened(
        new AudioInputFormat(16000, 1, 16, 2, AudioSampleEncoding.PcmSignedInteger));
    internal Exception? StartException { get; set; }
    internal Exception? StopException { get; set; }
    internal Exception? DisposeException { get; set; }
    internal int OpenCount { get; private set; }
    internal int StartCount { get; private set; }
    internal int StopCount { get; private set; }
    internal int DisposeCount { get; private set; }

    public event EventHandler<AudioCaptureData>? DataAvailable;
    public event EventHandler<AudioRuntimeStopped>? RecordingStopped;

    public Task<AudioRuntimeOpenResult> OpenAsync(
        AudioEndpointInfo endpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenCount++;
        return Task.FromResult(OpenResult);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        return StartException == null ? Task.CompletedTask : Task.FromException(StartException);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCount++;
        return StopException == null ? Task.CompletedTask : Task.FromException(StopException);
    }

    internal void EmitData(byte[] bytes, DateTimeOffset? timestamp = null) =>
        DataAvailable?.Invoke(this, new AudioCaptureData(
            bytes,
            timestamp ?? new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero)));

    internal void EmitStopped(AudioRuntimeStopReason reason, string? failure = null) =>
        RecordingStopped?.Invoke(this, new AudioRuntimeStopped(reason, failure));

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return DisposeException == null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(DisposeException);
    }
}

internal static class AudioTestData
{
    internal static byte[] Pcm16(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(index * 2, 2), samples[index]);
        }
        return bytes;
    }

    internal static byte[] ConstantPcm16Frame(short sample = 1000) =>
        Pcm16(Enumerable.Repeat(sample, NormalizedAudioFormat.SamplesPerFrame).ToArray());
}
