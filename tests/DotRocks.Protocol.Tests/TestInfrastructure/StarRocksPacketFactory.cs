using System.Text;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Protocol.Tests.TestInfrastructure;

/// <summary>
/// Builds the raw MySQL-protocol payloads a StarRocks server would send. This is the single
/// source of truth for wire-format literals in tests: parser tests feed these payloads directly,
/// and <see cref="FakeStarRocksServer"/> connection scripts write them to the socket.
/// </summary>
internal static class StarRocksPacketFactory
{
    /// <summary>First 8 bytes of the 20-byte mysql_native_password challenge.</summary>
    public static readonly byte[] AuthPart1 = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>
    /// Remaining 12 challenge bytes plus the trailing NUL the server appends on the wire.
    /// </summary>
    public static readonly byte[] AuthPart2 = [9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 0];

    /// <summary>
    /// The column-definition length-encoded marker announcing the 12 bytes of fixed-length
    /// fields that follow it: character set (2) + column length (4) + type (1) + flags (2) +
    /// decimals (1) + filler (2). The value is always 0x0C in a well-formed packet; malformed
    /// packet tests override it via <see cref="ColumnDefinition(string, byte, ulong)"/>.
    /// </summary>
    public const ulong ColumnDefinitionFixedFieldsLength = 0x0C;

    /// <summary>The utf8_general_ci collation id used for handshake and column character sets.</summary>
    public const byte Utf8GeneralCiCollation = 0x21;

    /// <summary>
    /// Builds the canonical fake-server greeting: protocol 10, a StarRocks-flavored server
    /// version, connection id 42, and the standard 4.1-protocol capability set.
    /// </summary>
    public static byte[] Handshake(string authPluginName, bool supportsTls = false)
    {
        CapabilityFlags capabilities =
            CapabilityFlags.LongPassword
            | CapabilityFlags.LongFlag
            | CapabilityFlags.Protocol41
            | CapabilityFlags.SecureConnection
            | CapabilityFlags.PluginAuth;
        if (supportsTls)
        {
            capabilities |= CapabilityFlags.Ssl;
        }

        return Handshake(
            protocolVersion: 10,
            serverVersion: "8.0.33-StarRocks-3.3",
            connectionId: 42,
            capabilities,
            characterSet: Utf8GeneralCiCollation,
            statusFlags: 2,
            authPluginDataLength: 21,
            AuthPart2,
            authPluginName
        );
    }

    /// <summary>Builds a fully parameterized protocol-10 server greeting payload.</summary>
    public static byte[] Handshake(
        byte protocolVersion,
        string serverVersion,
        uint connectionId,
        CapabilityFlags capabilities,
        byte characterSet,
        ushort statusFlags,
        byte authPluginDataLength,
        byte[] authPart2,
        string authPluginName
    )
    {
        uint caps = (uint)capabilities;
        using var writer = new ProtocolWriter();
        writer.WriteByte(protocolVersion);
        writer.WriteNullTerminatedString(serverVersion, Encoding.ASCII);
        writer.WriteFixedInteger(connectionId, 4);
        writer.WriteBytes(AuthPart1);
        writer.WriteByte(0); // filler
        writer.WriteFixedInteger(caps & 0xFFFF, 2);
        writer.WriteByte(characterSet);
        writer.WriteFixedInteger(statusFlags, 2);
        writer.WriteFixedInteger((caps >> 16) & 0xFFFF, 2);
        writer.WriteByte(authPluginDataLength);
        writer.WriteBytes(new byte[10]); // reserved
        writer.WriteBytes(authPart2);
        writer.WriteNullTerminatedString(authPluginName, Encoding.ASCII);
        return writer.ToArray();
    }

    /// <summary>Builds an OK payload: affected rows, last insert id 0, autocommit, no warnings.</summary>
    public static byte[] Ok(ulong affectedRows = 0)
    {
        using var writer = new ProtocolWriter();
        writer.WriteByte(ResultPacket.OkHeader);
        writer.WriteLengthEncodedInteger(affectedRows);
        writer.WriteLengthEncodedInteger(0); // last insert id
        writer.WriteFixedInteger(2, 2); // status flags: autocommit
        writer.WriteFixedInteger(0, 2); // warning count
        return writer.ToArray();
    }

    /// <summary>Builds an ERR payload with server error code 1064 and SQLSTATE 42000.</summary>
    public static byte[] Error(string message = "syntax error")
    {
        using var writer = new ProtocolWriter();
        writer.WriteByte(ProtocolConstants.ErrorPacketHeader);
        writer.WriteFixedInteger(1064, 2);
        writer.WriteByte((byte)'#'); // SQLSTATE marker
        writer.WriteBytes(Encoding.ASCII.GetBytes("42000"));
        writer.WriteBytes(Encoding.UTF8.GetBytes(message));
        return writer.ToArray();
    }

    /// <summary>EOF payload: 0xFE marker, no warnings, status flags 0x0002 (autocommit).</summary>
    public static byte[] Eof() => [0xFE, 0x00, 0x00, 0x02, 0x00];

    /// <summary>
    /// Builds a protocol-4.1 column-definition payload for a nullable column named
    /// <paramref name="name"/> with column length 1024 and utf8_general_ci character set.
    /// </summary>
    public static byte[] ColumnDefinition(
        string name,
        byte columnType = (byte)ColumnType.VarString,
        ulong fixedFieldsLength = ColumnDefinitionFixedFieldsLength
    )
    {
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedString("def", Encoding.UTF8); // catalog, always "def"
        writer.WriteLengthEncodedString(string.Empty, Encoding.UTF8); // schema
        writer.WriteLengthEncodedString(string.Empty, Encoding.UTF8); // table
        writer.WriteLengthEncodedString(string.Empty, Encoding.UTF8); // original table
        writer.WriteLengthEncodedString(name, Encoding.UTF8);
        writer.WriteLengthEncodedString(name, Encoding.UTF8); // original name
        writer.WriteLengthEncodedInteger(fixedFieldsLength);
        writer.WriteFixedInteger(Utf8GeneralCiCollation, 2);
        writer.WriteFixedInteger(1024, 4); // column length
        writer.WriteByte(columnType);
        writer.WriteFixedInteger(0, 2); // flags
        writer.WriteByte(0); // decimals
        writer.WriteFixedInteger(0, 2); // filler
        return writer.ToArray();
    }

    /// <summary>Builds a text-protocol row of length-encoded string values.</summary>
    public static byte[] TextRow(params string[] values)
    {
        using var writer = new ProtocolWriter();
        foreach (string value in values)
        {
            writer.WriteLengthEncodedString(value, Encoding.UTF8);
        }

        return writer.ToArray();
    }

    /// <summary>Builds a text-protocol row of length-encoded raw byte values.</summary>
    public static byte[] BinaryRow(params byte[][] values)
    {
        using var writer = new ProtocolWriter();
        foreach (byte[] value in values)
        {
            writer.WriteLengthEncodedBytes(value);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// Builds a COM_STMT_PREPARE response header (prepare-OK). The column-definition block that
    /// follows it on the wire is sent separately.
    /// </summary>
    public static byte[] PrepareOk(
        uint statementId = 9,
        ushort columnCount = 1,
        ushort parameterCount = 0
    )
    {
        using var writer = new ProtocolWriter();
        writer.WriteByte(0x00); // status
        writer.WriteFixedInteger(statementId, 4);
        writer.WriteFixedInteger(columnCount, 2);
        writer.WriteFixedInteger(parameterCount, 2);
        writer.WriteByte(0); // filler
        writer.WriteFixedInteger(0, 2); // warning count
        return writer.ToArray();
    }

    /// <summary>
    /// Frames the given payloads into a rewound in-memory packet stream, numbering packets from
    /// <paramref name="firstSequenceId"/>.
    /// </summary>
    public static MemoryStream PayloadStream(byte firstSequenceId, params byte[][] payloads)
    {
        var stream = new MemoryStream();
        var writer = new PacketWriter(stream);
        writer.ResetSequence(firstSequenceId);
        foreach (byte[] payload in payloads)
        {
            writer
                .WritePayloadAsync(payload, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        stream.Position = 0;
        return stream;
    }
}
