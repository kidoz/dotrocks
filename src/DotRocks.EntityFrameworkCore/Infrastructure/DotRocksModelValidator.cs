using System.Diagnostics.CodeAnalysis;
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
        }
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
}
