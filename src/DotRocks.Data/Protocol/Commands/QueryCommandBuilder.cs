using System.Text;

namespace DotRocks.Data.Protocol.Commands;

internal static class QueryCommandBuilder
{
    private const byte ComQuery = 0x03;

    public static byte[] Build(string commandText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        byte[] sqlBytes = Encoding.UTF8.GetBytes(commandText);
        byte[] payload = new byte[sqlBytes.Length + 1];
        payload[0] = ComQuery;
        sqlBytes.CopyTo(payload.AsSpan(1));
        return payload;
    }
}
