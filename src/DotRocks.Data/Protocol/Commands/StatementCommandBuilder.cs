using System.Text;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Commands;

/// <summary>
/// Builds the MySQL binary prepared-statement command payloads that StarRocks accepts:
/// <c>COM_STMT_PREPARE</c> and <c>COM_STMT_CLOSE</c>. <c>COM_STMT_EXECUTE</c> is added once its
/// binary parameter encoding is verified against a live server.
/// </summary>
internal static class StatementCommandBuilder
{
    private const byte ComStmtPrepare = 0x16;
    private const byte ComStmtClose = 0x19;

    public static byte[] BuildPrepare(string commandText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        byte[] sqlBytes = Encoding.UTF8.GetBytes(commandText);
        byte[] payload = new byte[sqlBytes.Length + 1];
        payload[0] = ComStmtPrepare;
        sqlBytes.CopyTo(payload.AsSpan(1));
        return payload;
    }

    public static byte[] BuildClose(uint statementId)
    {
        using var writer = new ProtocolWriter();
        writer.WriteByte(ComStmtClose);
        writer.WriteFixedInteger(statementId, 4);
        return writer.ToArray();
    }
}
