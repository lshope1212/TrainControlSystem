using System.Buffers.Binary;

namespace TrainControl.Common.Communication;

/// <summary>
/// Message framing for byte streams (TCP): every frame is a 4-byte big-endian payload length
/// followed by exactly that many payload bytes. TCP does not preserve message boundaries, so
/// readers must never assume one Read() == one message; <see cref="ReadFrameAsync"/> loops
/// until the whole header and payload have arrived.
/// </summary>
public static class LengthPrefixedFraming
{
    /// <summary>Upper bound on a frame's payload; larger lengths are treated as corrupt framing.</summary>
    public const int MaxPayloadBytes = 1 << 20;

    private const int HeaderBytes = 4;

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Frame payload of {payload.Length} bytes exceeds the {MaxPayloadBytes}-byte limit.");
        }

        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one complete frame. Returns null if the stream ended cleanly BEFORE a new frame
    /// started (peer closed the connection).
    /// </summary>
    /// <exception cref="InvalidDataException">Invalid length, or the stream ended part-way through a frame.</exception>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderBytes];
        var headerRead = await ReadAtMostAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < HeaderBytes)
        {
            throw new InvalidDataException("Stream ended inside a frame header.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Invalid frame length {length}.");
        }

        var payload = new byte[length];
        var payloadRead = await ReadAtMostAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead < length)
        {
            throw new InvalidDataException($"Stream ended inside a frame payload ({payloadRead} of {length} bytes).");
        }

        return payload;
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the stream ends; returns bytes read.</summary>
    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
