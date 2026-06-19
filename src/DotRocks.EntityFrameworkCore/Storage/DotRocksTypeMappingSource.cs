using System.Data;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data;
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
    private const int MaxExactDecimalPrecision = 29;

    private static readonly Dictionary<Type, RelationalTypeMapping> ClrMappings = new()
    {
        [typeof(bool)] = new BoolTypeMapping("boolean", DbType.Boolean),
        [typeof(sbyte)] = new SByteTypeMapping("tinyint", DbType.SByte),
        [typeof(byte)] = new ByteTypeMapping("tinyint unsigned", DbType.Byte),
        [typeof(short)] = new ShortTypeMapping("smallint", DbType.Int16),
        [typeof(int)] = new IntTypeMapping("int", DbType.Int32),
        [typeof(long)] = new LongTypeMapping("bigint", DbType.Int64),
        [typeof(float)] = new FloatTypeMapping("float", DbType.Single),
        [typeof(double)] = new DoubleTypeMapping("double", DbType.Double),
        [typeof(decimal)] = new DecimalTypeMapping("decimal", DbType.Decimal, null, null),
        [typeof(DotRocksDecimal)] = new DotRocksDecimalTypeMapping(),
        [typeof(string)] = new StringTypeMapping(
            "varchar",
            DbType.String,
            unicode: true,
            size: null
        ),
        [typeof(DateTime)] = new DateTimeTypeMapping("datetime", DbType.DateTime),
        [typeof(DateOnly)] = new DateOnlyTypeMapping("date", DbType.Date),
        [typeof(TimeOnly)] = new TimeOnlyTypeMapping("time", DbType.Time),
        [typeof(Guid)] = new DotRocksGuidTypeMapping(),
    };

    private static readonly RelationalTypeMapping JsonStringMapping = new StringTypeMapping(
        "json",
        DbType.String,
        unicode: true,
        size: null
    );

    private static readonly Dictionary<string, RelationalTypeMapping> StoreTypeMappings = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["boolean"] = ClrMappings[typeof(bool)],
        ["bool"] = ClrMappings[typeof(bool)],
        ["tinyint"] = ClrMappings[typeof(sbyte)],
        ["smallint"] = ClrMappings[typeof(short)],
        ["int"] = ClrMappings[typeof(int)],
        ["integer"] = ClrMappings[typeof(int)],
        ["mediumint"] = ClrMappings[typeof(int)],
        ["bigint"] = ClrMappings[typeof(long)],
        ["float"] = ClrMappings[typeof(float)],
        ["double"] = ClrMappings[typeof(double)],
        ["decimal"] = ClrMappings[typeof(decimal)],
        ["char"] = ClrMappings[typeof(string)],
        ["varchar"] = ClrMappings[typeof(string)],
        ["text"] = ClrMappings[typeof(string)],
        ["string"] = ClrMappings[typeof(string)],
        ["datetime"] = ClrMappings[typeof(DateTime)],
        ["date"] = ClrMappings[typeof(DateOnly)],
        ["time"] = ClrMappings[typeof(TimeOnly)],
        ["json"] = JsonStringMapping,
    };

    protected override RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        if (
            string.Equals(
                mappingInfo.StoreTypeNameBase,
                "largeint",
                StringComparison.OrdinalIgnoreCase
            )
            || mappingInfo.ClrType == typeof(Int128)
            || mappingInfo.ClrType == typeof(UInt128)
        )
        {
            return null;
        }

        if (
            (
                mappingInfo.StoreTypeNameBase is not null
                && string.Equals(
                    mappingInfo.StoreTypeNameBase,
                    "varbinary",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            || mappingInfo.ClrType == typeof(byte[])
        )
        {
            return null;
        }

        if (
            string.Equals(
                mappingInfo.StoreTypeNameBase,
                "decimal",
                StringComparison.OrdinalIgnoreCase
            ) || (mappingInfo.ClrType == typeof(decimal) && mappingInfo.Precision is not null)
        )
        {
            return FindDecimalMapping(mappingInfo);
        }

        Type? clrType = mappingInfo.ClrType;
        clrType = clrType is null ? null : Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (
            clrType is not null
            && clrType != typeof(string)
            && ClrMappings.TryGetValue(clrType, out RelationalTypeMapping? clrMapping)
        )
        {
            return clrMapping;
        }

        if (
            mappingInfo.StoreTypeNameBase is { } storeType
            && StoreTypeMappings.TryGetValue(storeType, out RelationalTypeMapping? storeMapping)
        )
        {
            return storeMapping;
        }

        if (clrType is null)
        {
            return null;
        }

        return ClrMappings.TryGetValue(clrType, out RelationalTypeMapping? mapping)
            ? mapping
            : null;
    }

    private static RelationalTypeMapping FindDecimalMapping(
        in RelationalTypeMappingInfo mappingInfo
    )
    {
        int? precision = mappingInfo.Precision;
        string storeType = mappingInfo.StoreTypeName ?? "decimal";
        Type? mappingClrType = mappingInfo.ClrType;
        Type? clrType = mappingClrType is null
            ? null
            : Nullable.GetUnderlyingType(mappingClrType) ?? mappingClrType;
        if (clrType == typeof(DotRocksDecimal) || precision > MaxExactDecimalPrecision)
        {
            return new DotRocksDecimalTypeMapping(storeType, precision, mappingInfo.Scale);
        }

        return new DecimalTypeMapping(storeType, DbType.Decimal, precision, mappingInfo.Scale);
    }
}
