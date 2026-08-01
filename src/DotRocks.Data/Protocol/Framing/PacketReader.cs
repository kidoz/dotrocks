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

    /// <summary>
    /// Reads one logical message payload into a pooled buffer. The caller must dispose the result
    /// once it has finished decoding, and must not retain anything that references the buffer.
    /// </summary>
    /// <remarks>
    /// Used by the result-row loop, where a fresh array per row was the read path's dominant
    /// allocation. A payload spanning continuation packets falls back to an exact-size array
    /// because its total length is not known until reassembly completes; those are rare (a value
    /// larger than one 16 MB packet) and the returned payload reports itself as unpooled.
    /// </remarks>
    public async ValueTask<PooledPayload> ReadPayloadPooledAsync(
        CancellationToken cancellationToken = default
    )
    {
        PacketHeader header = await ReadCheckedHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (header.PayloadLength >= _maxPayloadPerPacket)
        {
            byte[] reassembled = await ContinuePayloadAsync(header, cancellationToken)
                .ConfigureAwait(false);
            return new PooledPayload(reassembled, reassembled.Length, pooled: false);
        }

        if (header.PayloadLength > _maxLogicalPayloadLength)
        {
            throw LogicalPayloadTooLarge();
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(header.PayloadLength);
        try
        {
            await ReadExactAsync(buffer.AsMemory(0, header.PayloadLength), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return new PooledPayload(buffer, header.PayloadLength, pooled: true);
    }

    /// <summary>
    /// Synchronous counterpart to <see cref="ReadPayloadPooledAsync"/>.
    /// </summary>
    public PooledPayload ReadPayloadPooled()
    {
        PacketHeader header = ReadCheckedHeader();
        if (header.PayloadLength >= _maxPayloadPerPacket)
        {
            byte[] reassembled = ContinuePayload(header);
            return new PooledPayload(reassembled, reassembled.Length, pooled: false);
        }

        if (header.PayloadLength > _maxLogicalPayloadLength)
        {
            throw LogicalPayloadTooLarge();
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(header.PayloadLength);
        try
        {
            ReadExact(buffer.AsSpan(0, header.PayloadLength));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return new PooledPayload(buffer, header.PayloadLength, pooled: true);
    }

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

        return await ContinuePayloadAsync(header, cancellationToken).ConfigureAwait(false);
    }

    // Reassembles a payload that spans continuation packets, starting from its first header.
    private async ValueTask<byte[]> ContinuePayloadAsync(
        PacketHeader header,
        CancellationToken cancellationToken
    )
    {
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

    /// <summary>Synchronously reads one logical message payload.</summary>
    public byte[] ReadPayload()
    {
        PacketHeader header = ReadCheckedHeader();
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
            ReadExact(payload);
            return payload;
        }

        return ContinuePayload(header);
    }

    // Synchronous counterpart to ContinuePayloadAsync.
    private byte[] ContinuePayload(PacketHeader header)
    {
        var accumulator = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (header.PayloadLength > 0)
            {
                if (header.PayloadLength > _maxLogicalPayloadLength - accumulator.WrittenCount)
                {
                    throw LogicalPayloadTooLarge();
                }

                Span<byte> destination = accumulator.GetSpan(header.PayloadLength)[
                    ..header.PayloadLength
                ];
                ReadExact(destination);
                accumulator.Advance(header.PayloadLength);
            }

            if (header.PayloadLength < _maxPayloadPerPacket)
            {
                break;
            }

            header = ReadCheckedHeader();
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

    private PacketHeader ReadCheckedHeader()
    {
        PacketHeader header = ReadHeader();
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
        new(
            $"Logical packet payload exceeded the configured maximum of {_maxLogicalPayloadLength} byte(s)."
        )
        {
            IsPayloadTooLarge = true,
        };

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

    private PacketHeader ReadHeader()
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(MySqlPacket.HeaderLength);
        try
        {
            ReadExact(headerBuffer.AsSpan(0, MySqlPacket.HeaderLength));
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

    private void ReadExact(Span<byte> buffer)
    {
        try
        {
            _stream.ReadExactly(buffer);
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
