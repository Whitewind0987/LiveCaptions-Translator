using NAudio.CoreAudioApi;

namespace LiveCaptionsTranslator.audio.windows
{
    public sealed class WindowsAudioEndpointProvider : IAudioEndpointProvider
    {
        private int disposed;

        public Task<AudioEndpointEnumerationResult> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return Task.Run(() => EnumerateCore(cancellationToken), cancellationToken);
        }

        public async Task<AudioEndpointResolution> ResolveAsync(
            string? savedEndpointId,
            CancellationToken cancellationToken = default)
        {
            var enumeration = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
            if (!enumeration.Success)
                return AudioEndpointResolution.Unavailable(
                    enumeration.FailureReason ?? "Audio render endpoint enumeration failed.");

            return AudioEndpointResolver.Resolve(enumeration.Endpoints, savedEndpointId);
        }

        internal static IReadOnlyList<AudioEndpointInfo> OrderAndDeduplicate(
            IEnumerable<AudioEndpointInfo> endpoints) =>
            endpoints
                .GroupBy(endpoint => endpoint.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(endpoint => endpoint.IsDefault)
                .ThenBy(endpoint => endpoint.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(endpoint => endpoint.Id, StringComparer.Ordinal)
                .ToArray();

        private static AudioEndpointEnumerationResult EnumerateCore(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var enumerator = new MMDeviceEnumerator();
                string? defaultId = null;
                try
                {
                    using var defaultDevice = enumerator.GetDefaultAudioEndpoint(
                        DataFlow.Render,
                        Role.Multimedia);
                    defaultId = defaultDevice.ID;
                }
                catch
                {
                    // Absence of a default endpoint is represented by an empty default marker.
                }

                var endpoints = new List<AudioEndpointInfo>();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var device in devices)
                {
                    using (device)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        endpoints.Add(new AudioEndpointInfo(
                            device.ID,
                            device.FriendlyName,
                            string.Equals(device.ID, defaultId, StringComparison.Ordinal),
                            MapAvailability(device.State)));
                    }
                }

                return AudioEndpointEnumerationResult.Available(OrderAndDeduplicate(endpoints));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AudioEndpointEnumerationResult.Unavailable(
                    $"Unable to enumerate active Windows render endpoints: {ex.Message}");
            }
        }

        private static AudioEndpointAvailability MapAvailability(DeviceState state) => state switch
        {
            DeviceState.Active => AudioEndpointAvailability.Active,
            DeviceState.Disabled => AudioEndpointAvailability.Disabled,
            DeviceState.Unplugged => AudioEndpointAvailability.Unplugged,
            _ => AudioEndpointAvailability.Unknown
        };

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref disposed, 1);
            return ValueTask.CompletedTask;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(WindowsAudioEndpointProvider));
        }
    }
}
