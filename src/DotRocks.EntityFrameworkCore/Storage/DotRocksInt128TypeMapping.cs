using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;

namespace DotRocks.EntityFrameworkCore.Storage;

internal sealed class DotRocksInt128TypeMapping : RelationalTypeMapping
{
    public DotRocksInt128TypeMapping(string storeType = "largeint")
        : this(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(typeof(Int128)),
                storeType
            )
        ) { }

    private DotRocksInt128TypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters) { }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new DotRocksInt128TypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value) =>
        value switch
        {
            Int128 int128 => int128.ToString(CultureInfo.InvariantCulture),
            long longValue => longValue.ToString(CultureInfo.InvariantCulture),
            int intValue => intValue.ToString(CultureInfo.InvariantCulture),
            string text => Int128
                .Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Cannot generate a LARGEINT literal for value type '{value.GetType().FullName}'."
            ),
        };
}
