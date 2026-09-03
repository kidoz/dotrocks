using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;
using DataDbType = System.Data.DbType;

namespace DotRocks.EntityFrameworkCore.Storage;

/// <summary>
/// Maps <see cref="DateTime"/> to the StarRocks <c>DATETIME</c> type. EF's base mapping inlines
/// constants as <c>TIMESTAMP '…'</c> with seven fractional digits; StarRocks has no
/// <c>TIMESTAMP</c> literal and its <c>DATETIME</c> carries microseconds, so the typed
/// <c>DATETIME '…'</c> literal is emitted with six digits — the same precision the ADO.NET
/// parameter path binds.
/// </summary>
internal sealed class DotRocksDateTimeTypeMapping : DateTimeTypeMapping
{
    public DotRocksDateTimeTypeMapping()
        : base("datetime", DataDbType.DateTime) { }

    private DotRocksDateTimeTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters) { }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new DotRocksDateTimeTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value) =>
        value is DateTime dateTime
            ? "DATETIME '"
                + dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
                + "'"
            : throw new InvalidOperationException(
                $"Cannot generate a DATETIME literal for value type '{value.GetType().FullName}'."
            );
}
