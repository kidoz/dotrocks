using System.Buffers;

namespace DotRocks.Data.Protocol.Framing;

/// <summary>
/// Writes logical protocol messages to a stream, splitting payloads that exceed the per-packet
/// limit into continuation packets and emitting a trailing empty packet when the payload length is
/// an exact multiple of the limit, so the reader can detect the end of the message.
/// </summary>
internal sealed class PacketWriter
{
    private readonly Stream _stream;
    private readonly int _maxPayloadPerPacket;

    public PacketWriter(Stream stream, int maxPayloadPerPacket = MySqlPacket.MaxPacketPayloadLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadPerPacket, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maxPayloadPerPacket,
            MySqlPacket.MaxPacketPayloadLength
        );
        _stream = stream;
        _maxPayloadPerPacket = maxPayloadPerPacket;
    }

    /// <summary>The sequence id that will be stamped on the next packet.</summary>
    public byte SequenceId { get; private set; }

    /// <summary>Resets the sequence id at the start of a new command or protocol phase.</summary>
    public void ResetSequence(byte sequenceId = 0) => SequenceId = sequenceId;

    /// <summary>Writes <paramref name="payload"/> as one or more packets.</summary>
    public async ValueTask WritePayloadAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default
    )
    {
        int offset = 0;
        while (true)
        {
            int chunk = Math.Min(_maxPayloadPerPacket, payload.Length - offset);
            await WriteOnePacketAsync(payload.Slice(offset, chunk), cancellationToken)
                .ConfigureAwait(false);
            offset += chunk;

            // A packet shorter than the limit ends the message; a full-size packet means more
            // follows (including a trailing empty packet when the payload is an exact multiple).
            if (chunk < _maxPayloadPerPacket)
            {
                break;
            }
        }
    }

    /// <summary>Synchronously writes <paramref name="payload"/> as one or more packets.</summary>
    public void WritePayload(ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        while (true)
        {
            int chunk = Math.Min(_maxPayloadPerPacket, payload.Length - offset);
            WriteOnePacket(payload.Slice(offset, chunk));
            offset += chunk;
            if (chunk < _maxPayloadPerPacket)
            {
                break;
            }
        }
    }

    private async ValueTask WriteOnePacketAsync(
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken
    )
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(MySqlPacket.HeaderLength);
        try
        {
            new PacketHeader(chunk.Length, SequenceId).WriteTo(
                headerBuffer.AsSpan(0, MySqlPacket.HeaderLength)
            );
            await _stream
                .WriteAsync(headerBuffer.AsMemory(0, MySqlPacket.HeaderLength), cancellationToken)
                .ConfigureAwait(false);
            if (!chunk.IsEmpty)
            {
                await _stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }

            SequenceId = unchecked((byte)(SequenceId + 1));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private void WriteOnePacket(ReadOnlySpan<byte> chunk)
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(MySqlPacket.HeaderLength);
        try
        {
            new PacketHeader(chunk.Length, SequenceId).WriteTo(
                headerBuffer.AsSpan(0, MySqlPacket.HeaderLength)
            );
            _stream.Write(headerBuffer.AsSpan(0, MySqlPacket.HeaderLength));
            if (!chunk.IsEmpty)
            {
                _stream.Write(chunk);
            }

            SequenceId = unchecked((byte)(SequenceId + 1));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }
}
