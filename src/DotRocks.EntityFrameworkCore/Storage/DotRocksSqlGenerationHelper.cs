using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage;

namespace DotRocks.EntityFrameworkCore.Storage;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksSqlGenerationHelper(
    RelationalSqlGenerationHelperDependencies dependencies
) : RelationalSqlGenerationHelper(dependencies)
{
    public override string EscapeIdentifier(string identifier) =>
        identifier.Replace("`", "``", StringComparison.Ordinal);

    public override void EscapeIdentifier(StringBuilder builder, string identifier) =>
        builder.Append(EscapeIdentifier(identifier));

    public override string DelimitIdentifier(string identifier) =>
        "`" + EscapeIdentifier(identifier) + "`";

    public override void DelimitIdentifier(StringBuilder builder, string identifier)
    {
        builder.Append('`');
        EscapeIdentifier(builder, identifier);
        builder.Append('`');
    }

    public override string DelimitIdentifier(string name, string? schema) =>
        string.IsNullOrEmpty(schema)
            ? DelimitIdentifier(name)
            : DelimitIdentifier(schema) + "." + DelimitIdentifier(name);

    public override void DelimitIdentifier(StringBuilder builder, string name, string? schema)
    {
        if (!string.IsNullOrEmpty(schema))
        {
            DelimitIdentifier(builder, schema);
            builder.Append('.');
        }

        DelimitIdentifier(builder, name);
    }
}
