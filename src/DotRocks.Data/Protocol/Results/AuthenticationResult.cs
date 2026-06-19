using System.Text;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Results;

internal static class AuthenticationResult
{
    private const byte OkPacketHeader = 0x00;
    private const byte AuthSwitchRequestHeader = 0xFE;

    public static void Read(ReadOnlySpan<byte> payload, uint connectionId)
    {
        var reader = new ProtocolReader(payload);
        byte header = reader.ReadByte();
        switch (header)
        {
            case OkPacketHeader:
                return;
            case ProtocolConstants.ErrorPacketHeader:
                throw ReadError(reader, connectionId);
            case AuthSwitchRequestHeader:
                throw new DotRocksException(
                    "StarRocks requested an authentication switch, which DotRocks does not support yet."
                );
            default:
                throw new MalformedPacketException(
                    $"Unexpected authentication result packet header 0x{header:X2}."
                );
        }
    }

    private static DotRocksException ReadError(ProtocolReader reader, uint connectionId)
    {
        int errorCode = (int)reader.ReadFixedInteger(2);
        string? sqlState = null;
        if (reader.Remaining >= 6 && reader.PeekByte() == (byte)'#')
        {
            _ = reader.ReadByte();
            sqlState = Encoding.ASCII.GetString(reader.ReadBytes(5));
        }

        string message = Encoding.UTF8.GetString(reader.ReadToEnd());
        string sanitized = string.IsNullOrWhiteSpace(message)
            ? "StarRocks rejected authentication."
            : message;

        return new DotRocksException(
            sanitized,
            errorCode,
            sqlState,
            isTransient: false,
            connectionId
        );
    }
}
