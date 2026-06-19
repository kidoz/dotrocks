using System.Text;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Results;

internal static class AuthenticationResult
{
    private const byte AuthSwitchRequestHeader = 0xFE;

    public static void Read(ReadOnlySpan<byte> payload, uint connectionId)
    {
        var reader = new ProtocolReader(payload);
        byte header = reader.ReadByte();
        switch (header)
        {
            case ResultPacket.OkHeader:
                return;
            case ProtocolConstants.ErrorPacketHeader:
                throw ResultPacket.ReadError(payload, connectionId);
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
}
