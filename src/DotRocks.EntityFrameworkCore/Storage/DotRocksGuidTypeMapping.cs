using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using DataDbType = System.Data.DbType;

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
                DataDbType.String
            )
        ) { }

    private DotRocksGuidTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters) { }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new DotRocksGuidTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value) =>
        value switch
        {
            // A GUID never contains an escapable character, so quoting is enough; a string routed
            // through this mapping is arbitrary and must be fully escaped the StarRocks way.
            Guid guid => "'" + guid.ToString("D", CultureInfo.InvariantCulture) + "'",
            string text => DotRocksStringLiteral.Generate(text),
            _ => throw new InvalidOperationException(
                $"Cannot generate a GUID literal for value type '{value.GetType().FullName}'."
            ),
        };
}
