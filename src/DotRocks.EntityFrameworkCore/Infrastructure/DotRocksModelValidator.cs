using System.Diagnostics.CodeAnalysis;
using DotRocks.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotRocks.EntityFrameworkCore.Infrastructure;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksModelValidator(
    ModelValidatorDependencies dependencies,
    RelationalModelValidatorDependencies relationalDependencies
) : RelationalModelValidator(dependencies, relationalDependencies)
{
    public override void Validate(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger
    )
    {
        base.Validate(model, logger);

        foreach (IEntityType entityType in model.GetEntityTypes())
        {
            ValidateEntityType(entityType);
            ValidateTableShapeAnnotations(entityType);
        }

        ValidateSharedTableShapeAnnotations(model);
    }

    private static void ValidateEntityType(IEntityType entityType)
    {
        if (entityType.IsOwned())
        {
            throw new NotSupportedException(
                $"DotRocks EF Core does not support owned entity type '{entityType.DisplayName()}'."
            );
        }

        IKey? primaryKey = entityType.FindPrimaryKey();
        if (primaryKey is null)
        {
            return;
        }

        if (primaryKey.Properties.Count != 1)
        {
            throw new NotSupportedException(
                $"DotRocks EF Core writable entity type '{entityType.DisplayName()}' requires a single-column primary key; composite keys are not supported."
            );
        }

        if (entityType.GetNavigations().Any() || entityType.GetSkipNavigations().Any())
        {
            throw new NotSupportedException(
                $"DotRocks EF Core writable entity type '{entityType.DisplayName()}' must contain scalar properties only; navigations are not supported."
            );
        }

        if (entityType.GetComplexProperties().Any())
        {
            throw new NotSupportedException(
                $"DotRocks EF Core writable entity type '{entityType.DisplayName()}' must contain scalar properties only; complex properties are not supported."
            );
        }

        foreach (IProperty property in entityType.GetProperties())
        {
            ValidateProperty(entityType, property);
        }
    }

    private static void ValidateProperty(IEntityType entityType, IProperty property)
    {
        if (property.IsConcurrencyToken)
        {
            throw new NotSupportedException(
                $"DotRocks EF Core does not support concurrency token property '{entityType.DisplayName()}.{property.Name}'."
            );
        }

        if (property.ValueGenerated != ValueGenerated.Never)
        {
            throw new NotSupportedException(
                $"DotRocks EF Core requires explicit non-generated values; configure property '{entityType.DisplayName()}.{property.Name}' with ValueGeneratedNever()."
            );
        }

        if (
            property.GetDefaultValueSql() is not null
            || property.GetComputedColumnSql() is not null
        )
        {
            throw new NotSupportedException(
                $"DotRocks EF Core does not support generated/default SQL for property '{entityType.DisplayName()}.{property.Name}'."
            );
        }

        Type clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        string? storeType = property.GetColumnType();
        if (
            clrType == typeof(byte[])
            || clrType == typeof(UInt128)
            || string.Equals(storeType, "varbinary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storeType, "binary", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new NotSupportedException(
                $"DotRocks EF Core does not support property type '{property.ClrType.Name}' for '{entityType.DisplayName()}.{property.Name}'."
            );
        }
    }

    private static void ValidateTableShapeAnnotations(IEntityType entityType)
    {
        string? tableName = entityType.GetTableName();
        if (tableName is null)
        {
            return;
        }

        var table = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
        HashSet<string> storeColumns = entityType
            .GetProperties()
            .Select(property => property.GetColumnName(table))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        ValidateKeyModel(entityType);
        ValidateColumnAnnotation(
            entityType,
            DotRocksAnnotationNames.KeyColumns,
            storeColumns,
            "table key"
        );
        ValidateColumnAnnotation(
            entityType,
            DotRocksAnnotationNames.DistributionColumns,
            storeColumns,
            "hash distribution"
        );
        ValidatePositiveIntAnnotation(
            entityType,
            DotRocksAnnotationNames.DistributionBuckets,
            "hash distribution bucket count"
        );
        ValidatePositiveIntAnnotation(
            entityType,
            DotRocksAnnotationNames.ReplicationNum,
            "replication number"
        );
    }

    private static void ValidateSharedTableShapeAnnotations(IModel model)
    {
        foreach (
            IGrouping<(string? Schema, string Table), IEntityType> tableGroup in model
                .GetEntityTypes()
                .Where(entityType => entityType.GetTableName() is not null)
                .GroupBy(entityType => (entityType.GetSchema(), entityType.GetTableName()!))
        )
        {
            ValidateSharedTableAnnotation(tableGroup, DotRocksAnnotationNames.KeyModel);
            ValidateSharedTableAnnotation(tableGroup, DotRocksAnnotationNames.KeyColumns);
            ValidateSharedTableAnnotation(tableGroup, DotRocksAnnotationNames.DistributionColumns);
            ValidateSharedTableAnnotation(tableGroup, DotRocksAnnotationNames.DistributionBuckets);
            ValidateSharedTableAnnotation(tableGroup, DotRocksAnnotationNames.ReplicationNum);
        }
    }

    private static void ValidateSharedTableAnnotation(
        IEnumerable<IEntityType> entityTypes,
        string annotationName
    )
    {
        IAnnotation? firstAnnotation = null;
        string? tableName = null;
        foreach (IEntityType entityType in entityTypes)
        {
            tableName ??= entityType.GetSchemaQualifiedTableName();
            IAnnotation? annotation = entityType.FindAnnotation(annotationName);
            if (annotation is null)
            {
                continue;
            }

            if (
                firstAnnotation is not null
                && !AnnotationValueEquals(firstAnnotation.Value, annotation.Value)
            )
            {
                throw new NotSupportedException(
                    $"DotRocks EF Core migrations do not support conflicting '{annotationName}' table-shape annotations on shared table '{tableName}'."
                );
            }

            firstAnnotation = annotation;
        }
    }

    private static void ValidateKeyModel(IEntityType entityType)
    {
        object? value = entityType.FindAnnotation(DotRocksAnnotationNames.KeyModel)?.Value;
        if (value is null)
        {
            return;
        }

        if (
            value is DotRocksTableKeyModel.DuplicateKey
            || value is DotRocksTableKeyModel.PrimaryKey
            || value is string text
                && (
                    string.Equals(text, "DUPLICATE KEY", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "PRIMARY KEY", StringComparison.OrdinalIgnoreCase)
                )
        )
        {
            return;
        }

        throw new NotSupportedException(
            $"DotRocks EF Core migrations do not support StarRocks table key model '{value}'."
        );
    }

    private static void ValidateColumnAnnotation(
        IEntityType entityType,
        string annotationName,
        HashSet<string> storeColumns,
        string description
    )
    {
        object? value = entityType.FindAnnotation(annotationName)?.Value;
        string[]? columns = value switch
        {
            null => null,
            string[] stringArray => stringArray,
            IReadOnlyList<string> stringList => stringList.ToArray(),
            _ => throw new NotSupportedException(
                $"DotRocks EF Core migrations require {description} columns for '{entityType.DisplayName()}' to be configured as string store column names."
            ),
        };

        if (columns is null)
        {
            return;
        }

        if (columns.Length == 0 || columns.Any(string.IsNullOrWhiteSpace))
        {
            throw new NotSupportedException(
                $"DotRocks EF Core migrations require at least one non-empty {description} column for '{entityType.DisplayName()}'."
            );
        }

        foreach (string column in columns)
        {
            if (!storeColumns.Contains(column))
            {
                throw new NotSupportedException(
                    $"DotRocks EF Core migrations cannot use unknown store column '{column}' in the StarRocks {description} clause for '{entityType.DisplayName()}'."
                );
            }
        }
    }

    private static void ValidatePositiveIntAnnotation(
        IEntityType entityType,
        string annotationName,
        string description
    )
    {
        object? value = entityType.FindAnnotation(annotationName)?.Value;
        int? number = value switch
        {
            null => null,
            int intValue => intValue,
            _ => throw new NotSupportedException(
                $"DotRocks EF Core migrations require {description} for '{entityType.DisplayName()}' to be configured as a positive integer."
            ),
        };

        if (number <= 0)
        {
            throw new NotSupportedException(
                $"DotRocks EF Core migrations require {description} for '{entityType.DisplayName()}' to be greater than zero."
            );
        }
    }

    private static bool AnnotationValueEquals(object? left, object? right)
    {
        if (left is string[] leftArray && right is string[] rightArray)
        {
            return leftArray.SequenceEqual(rightArray, StringComparer.Ordinal);
        }

        if (left is IReadOnlyList<string> leftList && right is IReadOnlyList<string> rightList)
        {
            return leftList.SequenceEqual(rightList, StringComparer.Ordinal);
        }

        return Equals(left, right);
    }
}
