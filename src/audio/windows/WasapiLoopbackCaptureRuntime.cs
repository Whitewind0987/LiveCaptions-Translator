using System.Runtime.InteropServices;

using NAudio.CoreAudioApi;
using NAudio.Dmo;
using NAudio.Wave;

namespace LiveCaptionsTranslator.audio.windows
{
    public sealed class WasapiLoopbackCaptureRuntimeFactory : IAudioCaptureRuntimeFactory
    {
        public IAudioCaptureRuntime Create() => new WasapiLoopbackCaptureRuntime();
    }

    internal sealed record NativeCleanupStep(string Name, Action Action);

    internal sealed record NativeCleanupResult(IReadOnlyList<string> Failures)
    {
        internal bool Success => Failures.Count == 0;
        internal string? FailureReason => Success ? null : string.Join(" ", Failures);
    }

    internal static class NativeCleanupCoordinator
    {
        internal static NativeCleanupResult Run(params NativeCleanupStep[] steps)
        {
            var failures = new List<string>();
            foreach (var step in steps)
            {
                try { step.Action(); }
                catch (Exception ex) { failures.Add($"{step.Name} failed: {Describe(ex)}"); }
            }
            return new NativeCleanupResult(failures);
        }

        private static string Describe(Exception exception) => exception is AggregateException aggregate
            ? string.Join(" ", aggregate.Flatten().InnerExceptions.Select(Describe))
            : exception.Message;
    }

    public sealed class WasapiLoopbackCaptureRuntime : IAudioCaptureRuntime
    {
        private const int AudclntDeviceInvalidated = unchecked((int)0x88890004);

        private readonly SemaphoreSlim operationGate = new(1, 1);
        private readonly object stateLock = new();
        private MMDevice? device;
        private WasapiLoopbackCapture? capture;
        private bool started;
        private bool expectedStop;
        private int disposeStarted;

        public event EventHandler<AudioCaptureData>? DataAvailable;
        public event EventHandler<AudioRuntimeStopped>? RecordingStopped;

        public async Task<AudioRuntimeOpenResult> OpenAsync(
            AudioEndpointInfo endpoint,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ThrowIfDisposed();
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (capture != null)
                {
                    return AudioRuntimeOpenResult.Failed(
                        AudioCaptureState.Faulted,
                        "The WASAPI runtime is already open.");
                }

                try
                {
                    var opened = await Task.Run(() => OpenCore(endpoint, cancellationToken), cancellationToken)
                        .ConfigureAwait(false);
                    lock (stateLock)
                    {
                        device = opened.Device;
                        capture = opened.Capture;
                        expectedStop = false;
                        started = false;
                    }
                    return AudioRuntimeOpenResult.Opened(opened.Format);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var cleanup = CleanupNative(stopRecording: false);
                    return AudioRuntimeOpenResult.Failed(
                        IsDeviceUnavailable(ex) ? AudioCaptureState.Unavailable : AudioCaptureState.Faulted,
                        Combine(
                            $"Unable to open WASAPI loopback capture for '{endpoint.DisplayName}': {Describe(ex)}",
                            cleanup.FailureReason));
                }
            }
            finally
            {
                operationGate.Release();
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                WasapiLoopbackCapture current;
                lock (stateLock)
                {
                    current = capture ?? throw new InvalidOperationException("OpenAsync must succeed before StartAsync.");
                    if (started)
                        return;
                    started = true;
                    expectedStop = false;
                }

                try
                {
                    await Task.Run(current.StartRecording, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    lock (stateLock)
                        started = false;
                    throw;
                }
            }
            finally
            {
                operationGate.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bool shouldStop;
                lock (stateLock)
                {
                    shouldStop = started;
                    expectedStop = true;
                }

                var cleanup = await Task.Run(
                    () => CleanupNative(shouldStop),
                    CancellationToken.None).ConfigureAwait(false);
                if (!cleanup.Success)
                {
                    throw new AggregateException(
                        cleanup.Failures.Select(failure => new InvalidOperationException(failure)));
                }
            }
            finally
            {
                operationGate.Release();
            }
        }

        private (MMDevice Device, WasapiLoopbackCapture Capture, AudioInputFormat Format) OpenCore(
            AudioEndpointInfo endpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            var openedDevice = enumerator.GetDevice(endpoint.Id);
            WasapiLoopbackCapture? openedCapture = null;
            try
            {
                if (openedDevice.State != DeviceState.Active)
                    throw new InvalidOperationException("The selected render endpoint is not active.");

                openedCapture = new WasapiLoopbackCapture(openedDevice);
                var inputFormat = ToInputFormat(openedCapture.WaveFormat);
                openedCapture.DataAvailable += OnDataAvailable;
                openedCapture.RecordingStopped += OnRecordingStopped;
                return (openedDevice, openedCapture, inputFormat);
            }
            catch (Exception original)
            {
                var cleanup = NativeCleanupCoordinator.Run(
                    new("Data callback detach", () =>
                    {
                        if (openedCapture != null)
                            openedCapture.DataAvailable -= OnDataAvailable;
                    }),
                    new("Stopped callback detach", () =>
                    {
                        if (openedCapture != null)
                            openedCapture.RecordingStopped -= OnRecordingStopped;
                    }),
                    new("WASAPI capture disposal", () => openedCapture?.Dispose()),
                    new("MMDevice disposal", openedDevice.Dispose));
                if (!cleanup.Success)
                {
                    throw new AggregateException(
                        new[] { original }.Concat<Exception>(
                            cleanup.Failures.Select(failure => new InvalidOperationException(failure))));
                }
                throw;
            }
        }

        internal static AudioInputFormat ToInputFormat(WaveFormat waveFormat)
        {
            ArgumentNullException.ThrowIfNull(waveFormat);
            var encoding = waveFormat.Encoding switch
            {
                WaveFormatEncoding.IeeeFloat => AudioSampleEncoding.IeeeFloat,
                WaveFormatEncoding.Pcm => AudioSampleEncoding.PcmSignedInteger,
                WaveFormatEncoding.Extensible when waveFormat is WaveFormatExtensible extensible &&
                    extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT =>
                    AudioSampleEncoding.IeeeFloat,
                WaveFormatEncoding.Extensible when waveFormat is WaveFormatExtensible extensible &&
                    extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM =>
                    AudioSampleEncoding.PcmSignedInteger,
                _ => throw new NotSupportedException(
                    $"Unsupported WASAPI encoding '{waveFormat.Encoding}'.")
            };

            return new AudioInputFormat(
                waveFormat.SampleRate,
                waveFormat.Channels,
                waveFormat.BitsPerSample,
                waveFormat.BlockAlign,
                encoding);
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
        {
            try
            {
                bool accept;
                lock (stateLock)
                    accept = started && ReferenceEquals(sender, capture);
                if (!accept || eventArgs.BytesRecorded <= 0)
                    return;

                var ownedCopy = new byte[eventArgs.BytesRecorded];
                Buffer.BlockCopy(eventArgs.Buffer, 0, ownedCopy, 0, eventArgs.BytesRecorded);
                InvokeSafely(DataAvailable, new AudioCaptureData(ownedCopy, DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                InvokeSafely(RecordingStopped, new AudioRuntimeStopped(
                    AudioRuntimeStopReason.Faulted,
                    $"WASAPI data callback failed: {Describe(ex)}"));
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
        {
            bool wasExpected;
            lock (stateLock)
            {
                if (!ReferenceEquals(sender, capture))
                    return;
                wasExpected = expectedStop;
                started = false;
            }

            var exception = eventArgs.Exception;
            var cleanup = CleanupNative(stopRecording: false);
            var stopped = wasExpected
                ? new AudioRuntimeStopped(AudioRuntimeStopReason.Expected, cleanup.FailureReason)
                : exception != null && IsDeviceUnavailable(exception)
                    ? new AudioRuntimeStopped(
                        AudioRuntimeStopReason.Unavailable,
                        Combine(
                            $"The selected render endpoint became unavailable: {Describe(exception)}",
                            cleanup.FailureReason))
                    : new AudioRuntimeStopped(
                        AudioRuntimeStopReason.Faulted,
                        Combine(
                            exception == null
                                ? "WASAPI loopback capture stopped unexpectedly."
                                : $"WASAPI loopback capture failed: {Describe(exception)}",
                            cleanup.FailureReason));

            InvokeSafely(RecordingStopped, stopped);
        }

        private NativeCleanupResult CleanupNative(bool stopRecording)
        {
            WasapiLoopbackCapture? oldCapture;
            MMDevice? oldDevice;
            lock (stateLock)
            {
                oldCapture = capture;
                oldDevice = device;
                capture = null;
                device = null;
                started = false;
            }

            return NativeCleanupCoordinator.Run(
                new("StopRecording", () =>
                {
                    if (stopRecording && oldCapture != null)
                        oldCapture.StopRecording();
                }),
                new("Data callback detach", () =>
                {
                    if (oldCapture != null)
                        oldCapture.DataAvailable -= OnDataAvailable;
                }),
                new("Stopped callback detach", () =>
                {
                    if (oldCapture != null)
                        oldCapture.RecordingStopped -= OnRecordingStopped;
                }),
                new("WASAPI capture disposal", () => oldCapture?.Dispose()),
                new("MMDevice disposal", () => oldDevice?.Dispose()));
        }

        private static bool IsDeviceUnavailable(Exception exception) =>
            exception is AggregateException aggregate &&
                aggregate.Flatten().InnerExceptions.Any(IsDeviceUnavailable) ||
            exception is COMException comException && comException.HResult == AudclntDeviceInvalidated ||
            exception.HResult == AudclntDeviceInvalidated;

        private void InvokeSafely<T>(EventHandler<T>? handlers, T data)
        {
            if (handlers == null)
                return;
            foreach (EventHandler<T> handler in handlers.GetInvocationList())
            {
                try { handler(this, data); }
                catch (Exception) { /* Runtime consumers are isolated from the native callback. */ }
            }
        }

        private static string Combine(string original, string? cleanup) =>
            string.IsNullOrWhiteSpace(cleanup) ? original : $"{original} {cleanup}";

        private static string Describe(Exception exception) => exception is AggregateException aggregate
            ? string.Join(" ", aggregate.Flatten().InnerExceptions.Select(Describe))
            : exception.Message;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
                return;
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                operationGate.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposeStarted) != 0)
                throw new ObjectDisposedException(nameof(WasapiLoopbackCaptureRuntime));
        }
    }
}
