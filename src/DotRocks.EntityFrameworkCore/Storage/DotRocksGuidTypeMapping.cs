using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DotRocks.EntityFrameworkCore.Storage;

internal sealed class DotRocksGuidTypeMapping : RelationalTypeMapping
{
    private static readonly ValueConverter<Guid, string> GuidConverter = new(
        value => value.ToString("D", CultureInfo.InvariantCulture),
        value => Guid.Parse(value)
    );

    public DotRocksGuidTypeMapping(string storeType = "char(36)")
        : this(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(typeof(Guid), converter: GuidConverter),
                storeType,
                StoreTypePostfix.None,
                System.Data.DbType.String
            )
        ) { }

    private DotRocksGuidTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters) { }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new DotRocksGuidTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value) =>
        value switch
        {
            Guid guid => "'" + guid.ToString("D", CultureInfo.InvariantCulture) + "'",
            string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
            _ => throw new InvalidOperationException(
                $"Cannot generate a GUID literal for value type '{value.GetType().FullName}'."
            ),
        };
}
