using DotRocks.Data;
using Microsoft.EntityFrameworkCore.Storage;
using DataDbType = System.Data.DbType;

namespace DotRocks.EntityFrameworkCore.Storage;

internal sealed class DotRocksDecimalTypeMapping : RelationalTypeMapping
{
    public DotRocksDecimalTypeMapping(
        string storeType = "decimal",
        int? precision = null,
        int? scale = null
    )
        : this(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(typeof(DotRocksDecimal)),
                storeType,
                StoreTypePostfix.PrecisionAndScale,
                DataDbType.Decimal,
                precision: precision,
                scale: scale
            )
        ) { }

    private DotRocksDecimalTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters) { }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new DotRocksDecimalTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value) =>
        value switch
        {
            DotRocksDecimal dotRocksDecimal => dotRocksDecimal.ToString(),
            decimal decimalValue => DotRocksDecimal.FromDecimal(decimalValue).ToString(),
            _ => throw new InvalidOperationException(
                $"Cannot generate a DotRocks decimal literal for value type '{value.GetType().FullName}'."
            ),
        };
}
