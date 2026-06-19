namespace DotRocks.Data.Protocol.Serialization;

/// <summary>
/// Wire-level marker bytes for the StarRocks (MySQL-compatible) client protocol.
/// </summary>
/// <remarks>
/// StarRocks speaks a MySQL-compatible wire protocol. These constants describe only the subset
/// DotRocks implements; observed StarRocks behavior is authoritative where it differs from MySQL.
/// </remarks>
internal static class ProtocolConstants
{
    /// <summary>First byte values below this are encoded directly as a single-byte integer.</summary>
    public const byte LengthEncodedOneByteLimit = 0xFB;

    /// <summary>In a result-row value, this prefix means SQL NULL.</summary>
    public const byte NullValueMarker = 0xFB;

    /// <summary>Prefix indicating a 2-byte little-endian integer follows.</summary>
    public const byte LengthEncodedTwoBytePrefix = 0xFC;

    /// <summary>Prefix indicating a 3-byte little-endian integer follows.</summary>
    public const byte LengthEncodedThreeBytePrefix = 0xFD;

    /// <summary>Prefix indicating an 8-byte little-endian integer follows.</summary>
    public const byte LengthEncodedEightBytePrefix = 0xFE;

    /// <summary>ERR packet header; never a valid length-encoded integer prefix.</summary>
    public const byte ErrorPacketHeader = 0xFF;
}
