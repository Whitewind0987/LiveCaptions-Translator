using System.Collections.Concurrent;

using LiveCaptionsTranslator.captioning;
using LiveCaptionsTranslator.captioning.windows;

namespace LiveCaptionsTranslator.Tests;

internal sealed class FakeLiveCaptionsRuntime : ILiveCaptionsRuntime
{
    private readonly ConcurrentQueue<LiveCaptionsRuntimeInitializationResult> initializationResults = new();
    private readonly ConcurrentQueue<LiveCaptionsSnapshotReadResult> snapshotResults = new();
    private readonly SemaphoreSlim snapshotSignal = new(0);

    internal int InitializeCount { get; private set; }
    internal int ReadCount { get; private set; }
    internal int ShutdownCount { get; private set; }
    internal int ShowCount { get; private set; }
    internal int HideCount { get; private set; }
    internal int DisposeCount { get; private set; }
    internal bool IsWindowAvailable { get; set; } = true;
    internal bool? IsWindowVisible { get; set; } = false;
    internal CaptionWindowControlResult ShowResult { get; set; } = CaptionWindowControlResult.Completed();
    internal CaptionWindowControlResult HideResult { get; set; } = CaptionWindowControlResult.Completed();
    internal LiveCaptionsRuntimeCleanupResult CleanupResult { get; set; } =
        LiveCaptionsRuntimeCleanupResult.Cleaned();

    bool ILiveCaptionsRuntime.IsWindowAvailable => IsWindowAvailable;
    bool? ILiveCaptionsRuntime.IsWindowVisible => IsWindowVisible;

    internal void EnqueueInitialization(LiveCaptionsRuntimeInitializationResult result) =>
        initializationResults.Enqueue(result);

    internal void EnqueueSnapshot(LiveCaptionsSnapshotReadResult result)
    {
        snapshotResults.Enqueue(result);
        snapshotSignal.Release();
    }

    public Task<LiveCaptionsRuntimeInitializationResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeCount++;
        if (initializationResults.TryDequeue(out var result))
            return Task.FromResult(result);
        return Task.FromResult(LiveCaptionsRuntimeInitializationResult.Started());
    }

    public async Task<LiveCaptionsSnapshotReadResult> ReadSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        ReadCount++;
        try
        {
            await snapshotSignal.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return LiveCaptionsSnapshotReadResult.Cancelled();
        }

        return snapshotResults.TryDequeue(out var result)
            ? result
            : LiveCaptionsSnapshotReadResult.NoText();
    }

    public Task<CaptionWindowControlResult> ShowAsync(CancellationToken cancellationToken = default)
    {
        ShowCount++;
        if (ShowResult.Success)
            IsWindowVisible = true;
        return Task.FromResult(ShowResult);
    }

    public Task<CaptionWindowControlResult> HideAsync(CancellationToken cancellationToken = default)
    {
        HideCount++;
        if (HideResult.Success)
            IsWindowVisible = false;
        return Task.FromResult(HideResult);
    }

    public Task<LiveCaptionsRuntimeCleanupResult> ShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        ShutdownCount++;
        IsWindowAvailable = false;
        IsWindowVisible = null;
        return Task.FromResult(CleanupResult);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        snapshotSignal.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeCaptionSourceDelay : ICaptionSourceDelay
{
    private readonly TaskCompletionSource restartEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool BlockRestart { get; set; }
    internal int PollDelayCount { get; private set; }
    internal int RestartDelayCount { get; private set; }
    internal Task RestartEntered => restartEntered.Task;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay == WindowsLiveCaptionsSource.RestartDelay)
        {
            RestartDelayCount++;
            restartEntered.TrySetResult();
            if (BlockRestart)
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        else
        {
            PollDelayCount++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class FakeCaptionSource : ICaptionSource
{
    private readonly Guid sessionId = CaptionEventFactory.SessionA;

    internal int StartCount { get; private set; }
    internal int StopCount { get; private set; }
    internal int DisposeCount { get; private set; }
    internal bool EmitResetDuringStart { get; set; } = true;

    public string SourceId => "fake-caption-source";
    public CaptionSourceState State { get; private set; } = CaptionSourceState.Stopped;
    public string? FailureReason { get; private set; }

    public event EventHandler<CaptionEvent>? CaptionEventReceived;
    public event EventHandler<CaptionSourceStatus>? StatusChanged;

    public Task<CaptionSourceStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        State = CaptionSourceState.Starting;
        PublishStatus();
        if (EmitResetDuringStart)
            Emit(CaptionEventFactory.Reset(sessionId));
        State = CaptionSourceState.Running;
        FailureReason = null;
        PublishStatus();
        return Task.FromResult(CaptionSourceStartResult.Started(sessionId));
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCount++;
        State = CaptionSourceState.Stopped;
        PublishStatus();
        return Task.CompletedTask;
    }

    internal void Emit(CaptionEvent captionEvent) => CaptionEventReceived?.Invoke(this, captionEvent);

    internal void SetStatus(CaptionSourceState newState, string? reason = null)
    {
        State = newState;
        FailureReason = reason;
        PublishStatus();
    }

    private void PublishStatus() => StatusChanged?.Invoke(
        this,
        new CaptionSourceStatus(SourceId, State, FailureReason, DateTimeOffset.UtcNow));

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

internal static class AsyncTest
{
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }
}
