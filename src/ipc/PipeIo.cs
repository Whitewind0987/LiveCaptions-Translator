using System.IO;

namespace LiveCaptionsTranslator.ipc
{
    public static class PipeIo
    {
        public static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var offset = 0;
            while (offset < destination.Length)
            {
                var count = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new IpcProtocolException(ProtocolFailureKind.TruncatedMessage, "The pipe closed before the message was complete.");
                offset += count;
            }
        }

        public static async ValueTask WriteExactlyAsync(Stream stream, ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            const int chunkSize = 16 * 1024;
            for (var offset = 0; offset < source.Length; offset += chunkSize)
            {
                var count = Math.Min(chunkSize, source.Length - offset);
                await stream.WriteAsync(source.Slice(offset, count), cancellationToken).ConfigureAwait(false);
            }
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public sealed class IpcMessageStream : IAsyncDisposable
    {
        private readonly Stream stream;
        private readonly SemaphoreSlim writerGate = new(1, 1);
        private readonly int maximumPayload;
        private ulong nextSequence;
        private ulong lastReceivedSequence;
        private bool disposed;

        public IpcMessageStream(Stream stream, int maximumPayload = IpcProtocol.MaximumControlPayload)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.maximumPayload = maximumPayload;
        }

        public async Task WriteAsync(IpcMessageType type, ReadOnlyMemory<byte> payload, Guid correlationId = default, IpcMessageFlags flags = IpcMessageFlags.None, CancellationToken cancellationToken = default)
        {
            if (payload.Length > maximumPayload) throw new IpcProtocolException(ProtocolFailureKind.OversizedPayload, "Outgoing payload exceeds its bound.");
            await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var envelope = new IpcEnvelope(IpcProtocol.Major, IpcProtocol.Minor, (ushort)type, flags, (uint)payload.Length, ++nextSequence, correlationId);
                await PipeIo.WriteExactlyAsync(stream, envelope.Encode(), cancellationToken).ConfigureAwait(false);
                await PipeIo.WriteExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            }
            finally { writerGate.Release(); }
        }

        public async Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var header = new byte[IpcProtocol.EnvelopeSize];
                var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (first == 0) return null;
                await PipeIo.ReadExactlyAsync(stream, header.AsMemory(1), cancellationToken).ConfigureAwait(false);
                var envelope = IpcEnvelope.Decode(header, maximumPayload);
                if (envelope.Sequence <= lastReceivedSequence)
                    throw new IpcProtocolException(ProtocolFailureKind.OutOfOrder, "Message sequence is not strictly increasing.");
                lastReceivedSequence = envelope.Sequence;
                var payload = new byte[envelope.PayloadLength];
                await PipeIo.ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
                if (!envelope.IsKnownMessageType && envelope.Flags.HasFlag(IpcMessageFlags.Optional))
                    continue;
                return new IpcMessage(envelope, payload);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await stream.DisposeAsync().ConfigureAwait(false);
            writerGate.Dispose();
        }
    }
}
