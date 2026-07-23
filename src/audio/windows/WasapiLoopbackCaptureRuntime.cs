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
                    CleanupNative();
                    return AudioRuntimeOpenResult.Failed(
                        IsDeviceUnavailable(ex) ? AudioCaptureState.Unavailable : AudioCaptureState.Faulted,
                        $"Unable to open WASAPI loopback capture for '{endpoint.DisplayName}': {ex.Message}");
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
                WasapiLoopbackCapture? current;
                lock (stateLock)
                {
                    current = capture;
                    expectedStop = true;
                    started = false;
                }

                if (current != null)
                {
                    try
                    {
                        await Task.Run(current.StopRecording, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Native resources are still released below and cleanup diagnostics stay with the owner.
                    }
                }

                CleanupNative();
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
            catch
            {
                openedCapture?.Dispose();
                openedDevice.Dispose();
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
                    $"WASAPI data callback failed: {ex.Message}"));
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
            var stopped = wasExpected
                ? new AudioRuntimeStopped(AudioRuntimeStopReason.Expected, null)
                : exception != null && IsDeviceUnavailable(exception)
                    ? new AudioRuntimeStopped(AudioRuntimeStopReason.Unavailable,
                        $"The selected render endpoint became unavailable: {exception.Message}")
                    : new AudioRuntimeStopped(AudioRuntimeStopReason.Faulted,
                        exception == null
                            ? "WASAPI loopback capture stopped unexpectedly."
                            : $"WASAPI loopback capture failed: {exception.Message}");

            InvokeSafely(RecordingStopped, stopped);
            if (!wasExpected)
                CleanupNative();
        }

        private void CleanupNative()
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

            if (oldCapture != null)
            {
                oldCapture.DataAvailable -= OnDataAvailable;
                oldCapture.RecordingStopped -= OnRecordingStopped;
                oldCapture.Dispose();
            }
            oldDevice?.Dispose();
        }

        private static bool IsDeviceUnavailable(Exception exception) =>
            exception is COMException comException && comException.HResult == AudclntDeviceInvalidated ||
            exception.HResult == AudclntDeviceInvalidated;

        private void InvokeSafely<T>(EventHandler<T>? handlers, T data)
        {
            if (handlers == null)
                return;
            foreach (EventHandler<T> handler in handlers.GetInvocationList())
            {
                try { handler(this, data); }
                catch { }
            }
        }

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
