using System.Text;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Results;

internal static class ResultPacket
{
    public const byte OkHeader = 0x00;
    public const byte LocalInFileHeader = 0xFB;
    public const byte EofHeader = 0xFE;

    public static bool IsError(ReadOnlySpan<byte> payload) =>
        payload.Length > 0 && payload[0] == ProtocolConstants.ErrorPacketHeader;

    public static bool IsOk(ReadOnlySpan<byte> payload) =>
        payload.Length > 0 && payload[0] == OkHeader;

    public static bool IsEndOfResultSet(ReadOnlySpan<byte> payload) =>
        payload.Length > 0 && payload[0] == EofHeader && payload.Length < 9;

    public static DotRocksException ReadError(ReadOnlySpan<byte> payload, uint? connectionId)
    {
        var reader = new ProtocolReader(payload);
        byte header = reader.ReadByte();
        if (header != ProtocolConstants.ErrorPacketHeader)
        {
            throw new MalformedPacketException("Expected an ERR packet.");
        }

        int errorCode = (int)reader.ReadFixedInteger(2);
        string? sqlState = null;
        if (reader.Remaining > 0 && reader.PeekByte() == (byte)'#')
        {
            if (reader.Remaining < 6)
            {
                throw new MalformedPacketException("ERR packet SQLSTATE marker is truncated.");
            }

            _ = reader.ReadByte();
            sqlState = Encoding.ASCII.GetString(reader.ReadBytes(5));
        }

        string message = Encoding.UTF8.GetString(reader.ReadToEnd());
        return new DotRocksException(
            string.IsNullOrWhiteSpace(message) ? "StarRocks returned an error." : message,
            errorCode,
            sqlState,
            isTransient: false,
            connectionId
        );
    }

    public static OkResult ReadOk(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtocolReader(payload);
        byte header = reader.ReadByte();
        if (header != OkHeader)
        {
            throw new MalformedPacketException("Expected an OK packet.");
        }

        ulong affectedRows = reader.ReadLengthEncodedInteger();
        ulong lastInsertId = reader.ReadLengthEncodedInteger();
        ushort statusFlags = reader.Remaining >= 2 ? (ushort)reader.ReadFixedInteger(2) : (ushort)0;
        ushort warnings = reader.Remaining >= 2 ? (ushort)reader.ReadFixedInteger(2) : (ushort)0;
        long affectedRowsValue =
            affectedRows <= long.MaxValue
                ? (long)affectedRows
                : throw new MalformedPacketException(
                    "OK packet affected-row count exceeds Int64.MaxValue."
                );

        return new OkResult(affectedRowsValue, lastInsertId, statusFlags, warnings);
    }
}
