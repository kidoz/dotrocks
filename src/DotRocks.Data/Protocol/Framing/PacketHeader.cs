namespace DotRocks.Data.Protocol.Framing;

/// <summary>
/// A parsed packet header: the payload length (3-byte little-endian) and the sequence id.
/// </summary>
internal readonly struct PacketHeader
{
    public PacketHeader(int payloadLength, byte sequenceId)
    {
        if ((uint)payloadLength > MySqlPacket.MaxPacketPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadLength),
                payloadLength,
                $"Packet payload length must be between 0 and {MySqlPacket.MaxPacketPayloadLength}."
            );
        }

        PayloadLength = payloadLength;
        SequenceId = sequenceId;
    }

    /// <summary>Length of the payload that follows this header.</summary>
    public int PayloadLength { get; }

    /// <summary>Packet sequence id; increments per packet and resets at the start of a command.</summary>
    public byte SequenceId { get; }

    /// <summary>Parses a 4-byte header from <paramref name="header"/>.</summary>
    public static PacketHeader Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length < MySqlPacket.HeaderLength)
        {
            throw new ArgumentException(
                $"A packet header is {MySqlPacket.HeaderLength} bytes.",
                nameof(header)
            );
        }

        int length = header[0] | (header[1] << 8) | (header[2] << 16);
        return new PacketHeader(length, header[3]);
    }

    /// <summary>Writes this header into <paramref name="destination"/> (at least 4 bytes).</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < MySqlPacket.HeaderLength)
        {
            throw new ArgumentException(
                $"A packet header needs {MySqlPacket.HeaderLength} bytes.",
                nameof(destination)
            );
        }

        destination[0] = (byte)PayloadLength;
        destination[1] = (byte)(PayloadLength >> 8);
        destination[2] = (byte)(PayloadLength >> 16);
        destination[3] = SequenceId;
    }
}
