using System.Buffers;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Framing;

/// <summary>
/// Reads logical protocol messages from a stream, transparently reassembling multi-packet
/// payloads and verifying packet sequence ids. A connection that closes mid-message or a packet
/// arriving out of order raises <see cref="MalformedPacketException"/>.
/// </summary>
internal sealed class PacketReader
{
    public const int DefaultMaxLogicalPayloadLength = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly int _maxPayloadPerPacket;
    private readonly int _maxLogicalPayloadLength;

    public PacketReader(
        Stream stream,
        int maxPayloadPerPacket = MySqlPacket.MaxPacketPayloadLength,
        int maxLogicalPayloadLength = DefaultMaxLogicalPayloadLength
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadPerPacket, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maxPayloadPerPacket,
            MySqlPacket.MaxPacketPayloadLength
        );
        ArgumentOutOfRangeException.ThrowIfNegative(maxLogicalPayloadLength);
        _stream = stream;
        _maxPayloadPerPacket = maxPayloadPerPacket;
        _maxLogicalPayloadLength = maxLogicalPayloadLength;
    }

    /// <summary>The sequence id expected on the next packet.</summary>
    public byte SequenceId { get; private set; }

    /// <summary>Resets the expected sequence id at the start of a new command or protocol phase.</summary>
    public void ResetSequence(byte sequenceId = 0) => SequenceId = sequenceId;

    /// <summary>Reads one logical message payload, reassembling continuation packets.</summary>
    public async ValueTask<byte[]> ReadPayloadAsync(CancellationToken cancellationToken = default)
    {
        PacketHeader header = await ReadCheckedHeaderAsync(cancellationToken).ConfigureAwait(false);

        // Fast path: a first packet shorter than the per-packet maximum is the whole logical
        // payload, so read straight into an exact-size result buffer — no intermediate
        // ArrayBufferWriter and no trailing copy. This is the common single-packet row case.
        if (header.PayloadLength < _maxPayloadPerPacket)
        {
            if (header.PayloadLength > _maxLogicalPayloadLength)
            {
                throw LogicalPayloadTooLarge();
            }

            if (header.PayloadLength == 0)
            {
                return [];
            }

            byte[] payload = new byte[header.PayloadLength];
            await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false);
            return payload;
        }

        // Slow path: reassemble a payload that spans continuation packets.
        var accumulator = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (header.PayloadLength > 0)
            {
                if (header.PayloadLength > _maxLogicalPayloadLength - accumulator.WrittenCount)
                {
                    throw LogicalPayloadTooLarge();
                }

                Memory<byte> destination = accumulator.GetMemory(header.PayloadLength)[
                    ..header.PayloadLength
                ];
                await ReadExactAsync(destination, cancellationToken).ConfigureAwait(false);
                accumulator.Advance(header.PayloadLength);
            }

            if (header.PayloadLength < _maxPayloadPerPacket)
            {
                break;
            }

            header = await ReadCheckedHeaderAsync(cancellationToken).ConfigureAwait(false);
        }

        return accumulator.WrittenSpan.ToArray();
    }

    private async ValueTask<PacketHeader> ReadCheckedHeaderAsync(
        CancellationToken cancellationToken
    )
    {
        PacketHeader header = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (header.SequenceId != SequenceId)
        {
            throw new MalformedPacketException(
                $"Out-of-order packet: expected sequence id {SequenceId} but received {header.SequenceId}."
            );
        }

        SequenceId = unchecked((byte)(SequenceId + 1));
        return header;
    }

    private MalformedPacketException LogicalPayloadTooLarge() =>
        new($"Logical packet payload exceeded the configured maximum of {_maxLogicalPayloadLength} byte(s).");

    private async ValueTask<PacketHeader> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(MySqlPacket.HeaderLength);
        try
        {
            await ReadExactAsync(
                    headerBuffer.AsMemory(0, MySqlPacket.HeaderLength),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return PacketHeader.Parse(headerBuffer.AsSpan(0, MySqlPacket.HeaderLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await _stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new MalformedPacketException(
                "The connection closed before the expected packet bytes were received.",
                ex
            );
        }
    }
}
