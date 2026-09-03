using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;
using DataDbType = System.Data.DbType;

namespace DotRocks.EntityFrameworkCore.Storage;

/// <summary>
/// Maps <see cref="TimeOnly"/> to the StarRocks <c>TIME</c> type. EF's base mapping inlines
/// constants as <c>TIME '…'</c>, which StarRocks does not parse; a plain quoted string is what
/// the ADO.NET parameter path binds and what StarRocks converts implicitly.
/// </summary>
internal sealed class DotRocksTimeOnlyTypeMapping : TimeOnlyTypeMapping
{
    public DotRocksTimeOnlyTypeMapping()
        : base("time", DataDbType.Time) { }

    private DotRocksTimeOnlyTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters) { }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new DotRocksTimeOnlyTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value) =>
        value is TimeOnly time
            ? "'" + time.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'"
            : throw new InvalidOperationException(
                $"Cannot generate a TIME literal for value type '{value.GetType().FullName}'."
            );
}
