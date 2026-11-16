using System.Text;
using DotRocks.Data.Protocol.Commands;

namespace DotRocks.EntityFrameworkCore.Storage;

internal static class DotRocksStringLiteral
{
    public static string Generate(string value) => SqlStringLiteral.Generate(value);

    public static void Append(StringBuilder builder, string value) =>
        SqlStringLiteral.Append(builder, value);
}
