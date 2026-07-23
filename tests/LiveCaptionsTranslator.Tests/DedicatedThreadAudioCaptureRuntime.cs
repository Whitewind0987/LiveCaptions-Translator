using LiveCaptionsTranslator.audio;

namespace LiveCaptionsTranslator.Tests;

internal sealed class DedicatedThreadAudioCaptureRuntime : IAudioCaptureRuntime
{
    private readonly bool emitDuringStart;
    private readonly TaskCompletionSource<object?> dataRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> callbackReturned =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> stopRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> threadExited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? callbackThread;

    internal DedicatedThreadAudioCaptureRuntime(bool emitDuringStart = false)
    {
        this.emitDuringStart = emitDuringStart;
    }

    internal int StartCount { get; private set; }
    internal int StopCount { get; private set; }
    internal int DisposeCount { get; private set; }
    internal int CallbackThreadId { get; private set; }
    internal Task CallbackReturned => callbackReturned.Task;
    internal Task ThreadExited => threadExited.Task;

    public event EventHandler<AudioCaptureData>? DataAvailable;
    public event EventHandler<AudioRuntimeStopped>? RecordingStopped
    {
        add { }
        remove { }
    }

    public Task<AudioRuntimeOpenResult> OpenAsync(
        AudioEndpointInfo endpoint,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(AudioRuntimeOpenResult.Opened(
            new AudioInputFormat(16000, 1, 16, 2, AudioSampleEncoding.PcmSignedInteger)));

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        callbackThread = new Thread(RunCaptureThread)
        {
            IsBackground = true,
            Name = "Fake NAudio capture thread"
        };
        callbackThread.Start();
        if (emitDuringStart)
        {
            dataRequested.TrySetResult(null);
            await callbackReturned.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal void RequestData() => dataRequested.TrySetResult(null);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCount++;
        stopRequested.TrySetResult(null);
        dataRequested.TrySetResult(null);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        stopRequested.TrySetResult(null);
        dataRequested.TrySetResult(null);
        var thread = callbackThread;
        if (thread != null)
        {
            if (ReferenceEquals(Thread.CurrentThread, thread))
                threadExited.Task.GetAwaiter().GetResult();
            else
                thread.Join();
        }
        return ValueTask.CompletedTask;
    }

    private void RunCaptureThread()
    {
        try
        {
            CallbackThreadId = Environment.CurrentManagedThreadId;
            dataRequested.Task.GetAwaiter().GetResult();
            if (!stopRequested.Task.IsCompleted)
            {
                DataAvailable?.Invoke(this, new AudioCaptureData(
                    AudioTestData.ConstantPcm16Frame(),
                    new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero)));
            }
            callbackReturned.TrySetResult(null);
            stopRequested.Task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            callbackReturned.TrySetException(ex);
        }
        finally
        {
            threadExited.TrySetResult(null);
        }
    }
}
