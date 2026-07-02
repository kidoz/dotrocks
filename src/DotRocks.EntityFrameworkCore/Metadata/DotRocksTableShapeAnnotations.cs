using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DotRocks.EntityFrameworkCore.Metadata;

/// <summary>
/// The value kind a DotRocks table-shape annotation carries.
/// </summary>
internal enum DotRocksTableShapeValueKind
{
    /// <summary>A StarRocks table key model recognized by <see cref="DotRocksTableKeyModels"/>.</summary>
    KeyModel,

    /// <summary>An ordered, non-empty list of store column names.</summary>
    ColumnList,

    /// <summary>A positive integer.</summary>
    PositiveInt,

    /// <summary>A boolean flag.</summary>
    Flag,

    /// <summary>A string-to-string StarRocks table-property map.</summary>
    PropertyMap,
}

/// <summary>
/// Describes one DotRocks table-shape annotation: its name, the value kind it carries, the
/// clause description used in error messages, and whether its columns must map to store columns.
/// </summary>
internal sealed record DotRocksTableShapeAnnotation(
    string Name,
    DotRocksTableShapeValueKind ValueKind,
    string Description,
    bool ColumnsMustExist = false
);

/// <summary>
/// The single registry of DotRocks table-shape annotations. The relational annotation provider,
/// the model validator, the migrations SQL generator, and the design-time annotation code
/// generator all iterate or coerce through this registry, so a new StarRocks table option is
/// added in one place and cannot drift between those services.
/// </summary>
internal static class DotRocksTableShapeAnnotations
{
    /// <summary>Gets every DotRocks table-shape annotation, one entry per annotation name.</summary>
    public static IReadOnlyList<DotRocksTableShapeAnnotation> All { get; } =
    [
        new(
            DotRocksAnnotationNames.KeyModel,
            DotRocksTableShapeValueKind.KeyModel,
            "table key model"
        ),
        new(
            DotRocksAnnotationNames.KeyColumns,
            DotRocksTableShapeValueKind.ColumnList,
            "table key",
            ColumnsMustExist: true
        ),
        new(
            DotRocksAnnotationNames.DistributionColumns,
            DotRocksTableShapeValueKind.ColumnList,
            "hash distribution",
            ColumnsMustExist: true
        ),
        new(
            DotRocksAnnotationNames.DistributionBuckets,
            DotRocksTableShapeValueKind.PositiveInt,
            "distribution bucket count"
        ),
        new(
            DotRocksAnnotationNames.RandomDistribution,
            DotRocksTableShapeValueKind.Flag,
            "random distribution"
        ),
        new(
            DotRocksAnnotationNames.SortKeyColumns,
            DotRocksTableShapeValueKind.ColumnList,
            "sort key",
            ColumnsMustExist: true
        ),
        new(
            DotRocksAnnotationNames.Properties,
            DotRocksTableShapeValueKind.PropertyMap,
            "table properties"
        ),
        new(
            DotRocksAnnotationNames.ReplicationNum,
            DotRocksTableShapeValueKind.PositiveInt,
            "replication number"
        ),
    ];

    /// <summary>
    /// Coerces a column-list annotation value to store column names, or <see langword="null"/>
    /// when the annotation is absent. Throws for a wrong-typed, empty, or blank-entry list.
    /// </summary>
    public static string[]? CoerceColumnList(
        object? value,
        string description,
        string? target = null
    )
    {
        string[]? columns = value switch
        {
            null => null,
            string[] stringArray => stringArray,
            IReadOnlyList<string> stringList => [.. stringList],
            _ => throw new NotSupportedException(
                $"DotRocks EF Core migrations require {description} columns{For(target)} to be configured as string column names."
            ),
        };

        if (columns is not null && (columns.Length == 0 || columns.Any(string.IsNullOrWhiteSpace)))
        {
            throw new NotSupportedException(
                $"DotRocks EF Core migrations require at least one non-empty {description} column{For(target)}."
            );
        }

        return columns;
    }

    /// <summary>
    /// Coerces a positive-integer annotation value, or <see langword="null"/> when the annotation
    /// is absent. Throws for a wrong-typed or non-positive value.
    /// </summary>
    public static int? CoercePositiveInt(object? value, string description, string? target = null)
    {
        int? number = value switch
        {
            null => null,
            int intValue => intValue,
            _ => throw new NotSupportedException(
                $"DotRocks EF Core migrations require {description}{For(target)} to be configured as a positive integer."
            ),
        };

        if (number is <= 0)
        {
            throw new NotSupportedException(
                $"DotRocks EF Core migrations require {description}{For(target)} to be greater than zero."
            );
        }

        return number;
    }

    /// <summary>
    /// Coerces a boolean flag annotation value, treating an absent annotation as
    /// <see langword="false"/>. Throws for a wrong-typed value.
    /// </summary>
    public static bool CoerceFlag(object? value, string description, string? target = null) =>
        value switch
        {
            null => false,
            bool boolValue => boolValue,
            _ => throw new NotSupportedException(
                $"DotRocks EF Core migrations require {description}{For(target)} to be configured as a boolean."
            ),
        };

    /// <summary>
    /// Coerces a StarRocks table-property map annotation value, or <see langword="null"/> when the
    /// annotation is absent. Throws for a wrong-typed map or an empty property name.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? CoercePropertyMap(
        object? value,
        string? target = null
    )
    {
        IReadOnlyDictionary<string, string>? map = value switch
        {
            null => null,
            IReadOnlyDictionary<string, string> dictionary => dictionary,
            _ => throw new NotSupportedException(
                $"DotRocks EF Core migrations require StarRocks table properties{For(target)} to be configured as a string dictionary."
            ),
        };

        if (map is not null && map.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new NotSupportedException(
                "DotRocks EF Core migrations reject an empty StarRocks table property name."
            );
        }

        return map;
    }

    /// <summary>
    /// Coerces a key-model annotation value, or <see langword="null"/> when the annotation is
    /// absent. Throws for a value <see cref="DotRocksTableKeyModels"/> does not recognize.
    /// </summary>
    public static DotRocksTableKeyModel? CoerceKeyModel(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (DotRocksTableKeyModels.TryParse(value, out DotRocksTableKeyModel keyModel))
        {
            return keyModel;
        }

        throw new NotSupportedException(
            $"DotRocks EF Core migrations do not support StarRocks table key model '{value}'."
        );
    }

    /// <summary>
    /// Removes every table-shape annotation from <paramref name="annotations"/> and returns the
    /// design-time fluent API calls that reproduce them, using the canonical
    /// <see cref="DotRocksEntityTypeBuilderExtensions"/> method names.
    /// </summary>
    public static IReadOnlyList<MethodCallCodeFragment> GenerateFluentApiCalls(
        IDictionary<string, IAnnotation> annotations
    )
    {
        string[]? keyColumns = CoerceColumnList(
            Remove(annotations, DotRocksAnnotationNames.KeyColumns),
            "table key"
        );
        DotRocksTableKeyModel? keyModel = CoerceKeyModel(
            Remove(annotations, DotRocksAnnotationNames.KeyModel)
        );
        string[]? distributionColumns = CoerceColumnList(
            Remove(annotations, DotRocksAnnotationNames.DistributionColumns),
            "hash distribution"
        );
        int? distributionBuckets = CoercePositiveInt(
            Remove(annotations, DotRocksAnnotationNames.DistributionBuckets),
            "distribution bucket count"
        );
        bool randomDistribution = CoerceFlag(
            Remove(annotations, DotRocksAnnotationNames.RandomDistribution),
            "random distribution"
        );
        string[]? sortKeyColumns = CoerceColumnList(
            Remove(annotations, DotRocksAnnotationNames.SortKeyColumns),
            "sort key"
        );
        IReadOnlyDictionary<string, string>? properties = CoercePropertyMap(
            Remove(annotations, DotRocksAnnotationNames.Properties)
        );
        int? replicationNum = CoercePositiveInt(
            Remove(annotations, DotRocksAnnotationNames.ReplicationNum),
            "replication number"
        );

        var fragments = new List<MethodCallCodeFragment>();
        if (keyModel is { } model)
        {
            fragments.Add(
                new MethodCallCodeFragment(
                    GetKeyModelMethodName(model),
                    ToObjectArray(keyColumns ?? [])
                )
            );
        }

        // Random distribution is configured with its own fluent call; emitting a hash-distribution
        // call here would drop the RANDOM shape (and, with no columns, produce code that throws).
        if (randomDistribution)
        {
            fragments.Add(
                new MethodCallCodeFragment(
                    nameof(DotRocksEntityTypeBuilderExtensions.HasStarRocksRandomDistribution),
                    distributionBuckets ?? 1
                )
            );
        }
        else if (distributionColumns is not null || distributionBuckets is not null)
        {
            fragments.Add(
                new MethodCallCodeFragment(
                    nameof(DotRocksEntityTypeBuilderExtensions.HasStarRocksHashDistribution),
                    ToObjectArray(distributionBuckets ?? 1, distributionColumns ?? [])
                )
            );
        }

        if (sortKeyColumns is not null)
        {
            fragments.Add(
                new MethodCallCodeFragment(
                    nameof(DotRocksEntityTypeBuilderExtensions.HasStarRocksSortKey),
                    ToObjectArray(sortKeyColumns)
                )
            );
        }

        if (properties is not null)
        {
            foreach (KeyValuePair<string, string> property in properties)
            {
                fragments.Add(
                    new MethodCallCodeFragment(
                        nameof(DotRocksEntityTypeBuilderExtensions.HasStarRocksProperty),
                        property.Key,
                        property.Value
                    )
                );
            }
        }

        if (replicationNum is not null)
        {
            fragments.Add(
                new MethodCallCodeFragment(
                    nameof(DotRocksEntityTypeBuilderExtensions.HasStarRocksReplicationNum),
                    replicationNum
                )
            );
        }

        return fragments;
    }

    private static string GetKeyModelMethodName(DotRocksTableKeyModel keyModel) =>
        keyModel switch
        {
            DotRocksTableKeyModel.DuplicateKey => nameof(
                DotRocksEntityTypeBuilderExtensions.HasStarRocksDuplicateKey
            ),
            DotRocksTableKeyModel.PrimaryKey => nameof(
                DotRocksEntityTypeBuilderExtensions.HasStarRocksPrimaryKey
            ),
            DotRocksTableKeyModel.UniqueKey => nameof(
                DotRocksEntityTypeBuilderExtensions.HasStarRocksUniqueKey
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(keyModel),
                keyModel,
                "Unknown StarRocks table key model."
            ),
        };

    private static object? Remove(
        IDictionary<string, IAnnotation> annotations,
        string annotationName
    )
    {
        if (!annotations.TryGetValue(annotationName, out IAnnotation? annotation))
        {
            return null;
        }

        annotations.Remove(annotationName);
        return annotation.Value;
    }

    private static object[] ToObjectArray(params string[] values) => [.. values.Cast<object>()];

    private static object[] ToObjectArray(int first, string[] values)
    {
        object[] result = new object[values.Length + 1];
        result[0] = first;
        for (int i = 0; i < values.Length; i++)
        {
            result[i + 1] = values[i];
        }

        return result;
    }

    private static string For(string? target) => target is null ? string.Empty : $" for '{target}'";
}
