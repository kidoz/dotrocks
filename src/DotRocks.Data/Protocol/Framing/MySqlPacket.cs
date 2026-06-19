namespace DotRocks.Data.Protocol.Framing;

/// <summary>
/// Constants for the StarRocks (MySQL-compatible) packet framing layer: a 4-byte header
/// (3-byte little-endian payload length + 1-byte sequence id) followed by the payload.
/// </summary>
internal static class MySqlPacket
{
    /// <summary>Length of the packet header in bytes (3-byte length + 1-byte sequence id).</summary>
    public const int HeaderLength = 4;

    /// <summary>
    /// Maximum payload carried by a single packet (2^24 - 1). A payload of exactly this size
    /// signals that at least one more packet follows, so logical messages of this size or larger
    /// are split and reassembled.
    /// </summary>
    public const int MaxPacketPayloadLength = 0xFFFFFF;
}
