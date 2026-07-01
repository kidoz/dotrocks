using DotRocks.Data;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using DataDbType = System.Data.DbType;

namespace DotRocks.EntityFrameworkCore.Storage;

internal sealed class DotRocksDecimalTypeMapping : RelationalTypeMapping
{
    // Bridges a model-side System.Decimal property to the DotRocksDecimal the provider reads and
    // writes. Without a converter, EF would try to assign the DotRocksDecimal the reader returns
    // straight to a decimal property and fail materialization.
    private static readonly ValueConverter<decimal, DotRocksDecimal> DecimalConverter = new(
        value => DotRocksDecimal.FromDecimal(value),
        value => value.ToDecimal()
    );

    public DotRocksDecimalTypeMapping(
        string storeType = "decimal",
        int? precision = null,
        int? scale = null,
        bool modelIsDecimal = false
    )
        : this(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(
                    typeof(DotRocksDecimal),
                    modelIsDecimal ? DecimalConverter : null
                ),
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
