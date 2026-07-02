using System.Diagnostics;
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

        string target = entityType.DisplayName();
        foreach (DotRocksTableShapeAnnotation annotation in DotRocksTableShapeAnnotations.All)
        {
            object? value = entityType.FindAnnotation(annotation.Name)?.Value;
            if (value is null)
            {
                continue;
            }

            switch (annotation.ValueKind)
            {
                case DotRocksTableShapeValueKind.KeyModel:
                    _ = DotRocksTableShapeAnnotations.CoerceKeyModel(value);
                    break;
                case DotRocksTableShapeValueKind.ColumnList:
                    string[] columns = DotRocksTableShapeAnnotations.CoerceColumnList(
                        value,
                        annotation.Description,
                        target
                    )!;
                    if (annotation.ColumnsMustExist)
                    {
                        ValidateColumnsExist(columns, storeColumns, annotation.Description, target);
                    }

                    break;
                case DotRocksTableShapeValueKind.PositiveInt:
                    _ = DotRocksTableShapeAnnotations.CoercePositiveInt(
                        value,
                        annotation.Description,
                        target
                    );
                    break;
                case DotRocksTableShapeValueKind.Flag:
                    _ = DotRocksTableShapeAnnotations.CoerceFlag(
                        value,
                        annotation.Description,
                        target
                    );
                    break;
                case DotRocksTableShapeValueKind.PropertyMap:
                    _ = DotRocksTableShapeAnnotations.CoercePropertyMap(value, target);
                    break;
                default:
                    throw new UnreachableException(
                        $"Unknown table-shape annotation value kind '{annotation.ValueKind}'."
                    );
            }
        }
    }

    private static void ValidateColumnsExist(
        string[] columns,
        HashSet<string> storeColumns,
        string description,
        string target
    )
    {
        foreach (string column in columns)
        {
            if (!storeColumns.Contains(column))
            {
                throw new NotSupportedException(
                    $"DotRocks EF Core migrations cannot use unknown store column '{column}' in the StarRocks {description} clause for '{target}'."
                );
            }
        }
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
            foreach (DotRocksTableShapeAnnotation annotation in DotRocksTableShapeAnnotations.All)
            {
                ValidateSharedTableAnnotation(tableGroup, annotation.Name);
            }
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
                && !DotRocksAnnotationValues.AreEqual(firstAnnotation.Value, annotation.Value)
            )
            {
                throw new NotSupportedException(
                    $"DotRocks EF Core migrations do not support conflicting '{annotationName}' table-shape annotations on shared table '{tableName}'."
                );
            }

            firstAnnotation = annotation;
        }
    }
}
