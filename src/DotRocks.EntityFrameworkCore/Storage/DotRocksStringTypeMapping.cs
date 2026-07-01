using Microsoft.EntityFrameworkCore.Storage;
using DataDbType = System.Data.DbType;

namespace DotRocks.EntityFrameworkCore.Storage;

/// <summary>
/// String mapping that generates StarRocks string literals. The base relational
/// <see cref="StringTypeMapping"/> only doubles single quotes; StarRocks additionally treats
/// backslash as an escape character, so a value ending in a backslash (or containing a control
/// character) would corrupt the literal or break out of it. This mapping escapes literals the same
/// way the native driver does.
/// </summary>
internal sealed class DotRocksStringTypeMapping : StringTypeMapping
{
    public DotRocksStringTypeMapping(
        string storeType,
        DataDbType? dbType = DataDbType.String,
        bool unicode = false,
        int? size = null
    )
        : base(storeType, dbType, unicode, size) { }

    private DotRocksStringTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters) { }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new DotRocksStringTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value) =>
        value is string text
            ? DotRocksStringLiteral.Generate(text)
            : throw new InvalidOperationException(
                $"Cannot generate a string literal for value type '{value.GetType().FullName}'."
            );
}
