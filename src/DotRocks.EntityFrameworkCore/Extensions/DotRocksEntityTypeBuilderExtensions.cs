using DotRocks.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Extension methods for configuring StarRocks table-shape annotations used by DotRocks EF Core migrations.
/// </summary>
public static class DotRocksEntityTypeBuilderExtensions
{
    /// <summary>
    /// Configures the entity's migration table as a StarRocks <c>DUPLICATE KEY</c> table.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="columns">The key columns to emit in the StarRocks table key clause.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static EntityTypeBuilder HasStarRocksDuplicateKey(
        this EntityTypeBuilder entityTypeBuilder,
        params string[] columns
    ) => SetKeyModel(entityTypeBuilder, DotRocksTableKeyModel.DuplicateKey, columns);

    /// <summary>
    /// Configures the entity's migration table as a StarRocks <c>PRIMARY KEY</c> table.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="columns">The key columns to emit in the StarRocks table key clause.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static EntityTypeBuilder HasStarRocksPrimaryKey(
        this EntityTypeBuilder entityTypeBuilder,
        params string[] columns
    ) => SetKeyModel(entityTypeBuilder, DotRocksTableKeyModel.PrimaryKey, columns);

    /// <summary>
    /// Configures the entity's migration table as a StarRocks <c>UNIQUE KEY</c> table.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="columns">The key columns to emit in the StarRocks table key clause.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static EntityTypeBuilder HasStarRocksUniqueKey(
        this EntityTypeBuilder entityTypeBuilder,
        params string[] columns
    ) => SetKeyModel(entityTypeBuilder, DotRocksTableKeyModel.UniqueKey, columns);

    /// <summary>
    /// Configures StarRocks hash distribution for migrations generated for this entity.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="buckets">The positive bucket count for the <c>DISTRIBUTED BY HASH</c> clause.</param>
    /// <param name="columns">The columns to emit in the StarRocks hash distribution clause.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static EntityTypeBuilder HasStarRocksHashDistribution(
        this EntityTypeBuilder entityTypeBuilder,
        int buckets,
        params string[] columns
    )
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buckets);
        string[] normalizedColumns = ValidateColumns(columns);
        entityTypeBuilder.Metadata.SetAnnotation(
            DotRocksAnnotationNames.DistributionColumns,
            normalizedColumns
        );
        entityTypeBuilder.Metadata.SetAnnotation(
            DotRocksAnnotationNames.DistributionBuckets,
            buckets
        );
        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures the StarRocks <c>replication_num</c> table property for migrations generated for this entity.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="replicationNum">The positive replication number.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static EntityTypeBuilder HasStarRocksReplicationNum(
        this EntityTypeBuilder entityTypeBuilder,
        int replicationNum
    )
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replicationNum);
        entityTypeBuilder.Metadata.SetAnnotation(
            DotRocksAnnotationNames.ReplicationNum,
            replicationNum
        );
        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures StarRocks random distribution (<c>DISTRIBUTED BY RANDOM</c>) for migrations
    /// generated for this entity, instead of hash distribution.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="buckets">The positive bucket count for the <c>DISTRIBUTED BY RANDOM</c> clause.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static EntityTypeBuilder HasStarRocksRandomDistribution(
        this EntityTypeBuilder entityTypeBuilder,
        int buckets = 1
    )
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buckets);
        entityTypeBuilder.Metadata.SetAnnotation(DotRocksAnnotationNames.RandomDistribution, true);
        entityTypeBuilder.Metadata.SetAnnotation(
            DotRocksAnnotationNames.DistributionBuckets,
            buckets
        );
        entityTypeBuilder.Metadata.RemoveAnnotation(DotRocksAnnotationNames.DistributionColumns);
        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures StarRocks random distribution for migrations generated for this entity. This is
    /// an alias for <see cref="HasStarRocksRandomDistribution"/>, the canonical name.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="buckets">The positive bucket count for the <c>DISTRIBUTED BY RANDOM</c> clause.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static EntityTypeBuilder DistributedRandomly(
        this EntityTypeBuilder entityTypeBuilder,
        int buckets
    ) => HasStarRocksRandomDistribution(entityTypeBuilder, buckets);

    /// <summary>
    /// Configures the StarRocks sort key (<c>ORDER BY</c>) for migrations generated for this entity.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="columns">The sort-key columns to emit in the StarRocks <c>ORDER BY</c> clause.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static EntityTypeBuilder HasStarRocksSortKey(
        this EntityTypeBuilder entityTypeBuilder,
        params string[] columns
    )
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        entityTypeBuilder.Metadata.SetAnnotation(
            DotRocksAnnotationNames.SortKeyColumns,
            ValidateColumns(columns)
        );
        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures the StarRocks sort key for migrations generated for this entity. This is an
    /// alias for <see cref="HasStarRocksSortKey"/>, the canonical name.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="columns">The sort-key columns to emit in the StarRocks <c>ORDER BY</c> clause.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static EntityTypeBuilder HasSortKey(
        this EntityTypeBuilder entityTypeBuilder,
        params string[] columns
    ) => HasStarRocksSortKey(entityTypeBuilder, columns);

    /// <summary>
    /// Adds a StarRocks table property (a <c>PROPERTIES</c> entry) for migrations generated for this
    /// entity. Names and values are validated and quote-escaped.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="name">The property name.</param>
    /// <param name="value">The property value.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static EntityTypeBuilder HasStarRocksProperty(
        this EntityTypeBuilder entityTypeBuilder,
        string name,
        string value
    )
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (
            entityTypeBuilder.Metadata.FindAnnotation(DotRocksAnnotationNames.Properties)?.Value
            is IReadOnlyDictionary<string, string> existing
        )
        {
            foreach (KeyValuePair<string, string> entry in existing)
            {
                properties[entry.Key] = entry.Value;
            }
        }

        properties[name] = value;
        entityTypeBuilder.Metadata.SetAnnotation(DotRocksAnnotationNames.Properties, properties);
        return entityTypeBuilder;
    }

    private static EntityTypeBuilder SetKeyModel(
        EntityTypeBuilder entityTypeBuilder,
        DotRocksTableKeyModel keyModel,
        string[] columns
    )
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        string[] normalizedColumns = ValidateColumns(columns);
        entityTypeBuilder.Metadata.SetAnnotation(DotRocksAnnotationNames.KeyModel, keyModel);
        entityTypeBuilder.Metadata.SetAnnotation(
            DotRocksAnnotationNames.KeyColumns,
            normalizedColumns
        );
        return entityTypeBuilder;
    }

    private static string[] ValidateColumns(string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Length == 0)
        {
            throw new ArgumentException(
                "At least one StarRocks table-shape column is required.",
                nameof(columns)
            );
        }

        string[] normalizedColumns = new string[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            string column = columns[i];
            if (string.IsNullOrWhiteSpace(column))
            {
                throw new ArgumentException(
                    "StarRocks table-shape columns cannot be empty.",
                    nameof(columns)
                );
            }

            normalizedColumns[i] = column;
        }

        return normalizedColumns;
    }
}
