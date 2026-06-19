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

    /// <summary>Resets the expected sequence id to 0 at the start of a new command.</summary>
    public void ResetSequence() => SequenceId = 0;

    /// <summary>Reads one logical message payload, reassembling continuation packets.</summary>
    public async ValueTask<byte[]> ReadPayloadAsync(CancellationToken cancellationToken = default)
    {
        var accumulator = new ArrayBufferWriter<byte>();
        while (true)
        {
            PacketHeader header = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            if (header.SequenceId != SequenceId)
            {
                throw new MalformedPacketException(
                    $"Out-of-order packet: expected sequence id {SequenceId} but received {header.SequenceId}."
                );
            }

            SequenceId = unchecked((byte)(SequenceId + 1));

            if (header.PayloadLength > 0)
            {
                if (header.PayloadLength > _maxLogicalPayloadLength - accumulator.WrittenCount)
                {
                    throw new MalformedPacketException(
                        $"Logical packet payload exceeded the configured maximum of {_maxLogicalPayloadLength} byte(s)."
                    );
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
        }

        return accumulator.WrittenSpan.ToArray();
    }

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
