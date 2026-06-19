using System.Data;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Storage;

namespace DotRocks.EntityFrameworkCore.Storage;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksTypeMappingSource(
    TypeMappingSourceDependencies dependencies,
    RelationalTypeMappingSourceDependencies relationalDependencies
) : RelationalTypeMappingSource(dependencies, relationalDependencies)
{
    private static readonly Dictionary<Type, RelationalTypeMapping> ClrMappings = new()
    {
        [typeof(bool)] = new BoolTypeMapping("boolean", DbType.Boolean),
        [typeof(byte)] = new ByteTypeMapping("tinyint", DbType.Byte),
        [typeof(short)] = new ShortTypeMapping("smallint", DbType.Int16),
        [typeof(int)] = new IntTypeMapping("int", DbType.Int32),
        [typeof(long)] = new LongTypeMapping("bigint", DbType.Int64),
        [typeof(float)] = new FloatTypeMapping("float", DbType.Single),
        [typeof(double)] = new DoubleTypeMapping("double", DbType.Double),
        [typeof(decimal)] = new DecimalTypeMapping("decimal", DbType.Decimal),
        [typeof(string)] = new StringTypeMapping(
            "varchar",
            DbType.String,
            unicode: true,
            size: null
        ),
        [typeof(DateTime)] = new DateTimeTypeMapping("datetime", DbType.DateTime),
        [typeof(DateOnly)] = new DateOnlyTypeMapping("date", DbType.Date),
        [typeof(TimeOnly)] = new TimeOnlyTypeMapping("time", DbType.Time),
        [typeof(Guid)] = new GuidTypeMapping("char(36)", DbType.Guid),
        [typeof(byte[])] = new ByteArrayTypeMapping("varbinary", DbType.Binary, size: null),
    };

    private static readonly Dictionary<string, RelationalTypeMapping> StoreTypeMappings = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["boolean"] = ClrMappings[typeof(bool)],
        ["bool"] = ClrMappings[typeof(bool)],
        ["tinyint"] = ClrMappings[typeof(byte)],
        ["smallint"] = ClrMappings[typeof(short)],
        ["int"] = ClrMappings[typeof(int)],
        ["integer"] = ClrMappings[typeof(int)],
        ["bigint"] = ClrMappings[typeof(long)],
        ["float"] = ClrMappings[typeof(float)],
        ["double"] = ClrMappings[typeof(double)],
        ["decimal"] = ClrMappings[typeof(decimal)],
        ["varchar"] = ClrMappings[typeof(string)],
        ["string"] = ClrMappings[typeof(string)],
        ["datetime"] = ClrMappings[typeof(DateTime)],
        ["date"] = ClrMappings[typeof(DateOnly)],
        ["time"] = ClrMappings[typeof(TimeOnly)],
        ["varbinary"] = ClrMappings[typeof(byte[])],
    };

    protected override RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        if (
            mappingInfo.StoreTypeNameBase is { } storeType
            && StoreTypeMappings.TryGetValue(storeType, out RelationalTypeMapping? storeMapping)
        )
        {
            return storeMapping;
        }

        Type? clrType = mappingInfo.ClrType;
        if (clrType is null)
        {
            return null;
        }

        clrType = Nullable.GetUnderlyingType(clrType) ?? clrType;
        return ClrMappings.TryGetValue(clrType, out RelationalTypeMapping? mapping)
            ? mapping
            : null;
    }
}
