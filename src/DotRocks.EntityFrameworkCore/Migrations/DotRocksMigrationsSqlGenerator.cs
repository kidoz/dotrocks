using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotRocks.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace DotRocks.EntityFrameworkCore.Migrations;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies
) : MigrationsSqlGenerator(dependencies)
{
    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        if (operation.PrimaryKey is null || operation.PrimaryKey.Columns.Length == 0)
        {
            throw new NotSupportedException(
                $"DotRocks EF Core migrations require a primary key for table '{operation.Name}'."
            );
        }

        string[] keyColumns = GetColumnListAnnotation(
            operation,
            DotRocksAnnotationNames.KeyColumns,
            operation.PrimaryKey.Columns,
            "table key"
        );
        string[] distributionColumns = GetColumnListAnnotation(
            operation,
            DotRocksAnnotationNames.DistributionColumns,
            keyColumns,
            "hash distribution"
        );
        int distributionBuckets = GetPositiveIntAnnotation(
            operation,
            DotRocksAnnotationNames.DistributionBuckets,
            1,
            "hash distribution bucket count"
        );
        int replicationNum = GetPositiveIntAnnotation(
            operation,
            DotRocksAnnotationNames.ReplicationNum,
            1,
            "replication number"
        );
        string keyClause = GetKeyClause(operation);

        ValidateColumnsExist(operation, keyColumns, "table key");
        ValidateColumnsExist(operation, distributionColumns, "hash distribution");
        ValidateKeyColumnTypes(operation, keyColumns);

        builder.Append("CREATE TABLE ");
        builder.Append(
            Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema)
        );
        builder.AppendLine(" (");
        for (int i = 0; i < operation.Columns.Count; i++)
        {
            builder.Append("    ");
            ColumnDefinition(operation.Columns[i], model, builder);
            if (i < operation.Columns.Count - 1)
            {
                builder.Append(",");
            }

            builder.AppendLine();
        }

        builder.Append(") ").Append(keyClause).Append("(");
        AppendDelimitedColumnList(builder, keyColumns);
        builder.AppendLine(")");
        builder.Append("DISTRIBUTED BY HASH(");
        AppendDelimitedColumnList(builder, distributionColumns);
        builder
            .Append(") BUCKETS ")
            .Append(distributionBuckets.ToString(CultureInfo.InvariantCulture))
            .AppendLine();
        builder
            .Append("PROPERTIES ('replication_num' = '")
            .Append(replicationNum.ToString(CultureInfo.InvariantCulture))
            .Append("')");

        if (terminate)
        {
            EndStatement(builder, suppressTransaction: true);
        }
    }

    protected override void Generate(
        EnsureSchemaOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        builder
            .Append("CREATE DATABASE IF NOT EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder, suppressTransaction: true);
    }

    protected override void Generate(
        DropSchemaOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        throw CreateUnsupportedMigrationOperationException("DROP DATABASE");
    }

    protected override void Generate(
        DropTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        builder.Append("DROP TABLE ");
        builder.Append(
            Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema)
        );
        if (terminate)
        {
            EndStatement(builder, suppressTransaction: true);
        }
    }

    protected override void Generate(
        SqlOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        if (
            operation
                .Sql.TrimStart()
                .StartsWith("TRUNCATE TABLE", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw CreateUnsupportedMigrationOperationException("TRUNCATE TABLE");
        }

        base.Generate(operation, model, builder);
    }

    protected override void ColumnDefinition(
        string? schema,
        string table,
        string name,
        ColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        if (
            operation.ComputedColumnSql is not null
            || operation.DefaultValue is not null
            || operation.DefaultValueSql is not null
        )
        {
            throw new NotSupportedException(
                $"DotRocks EF Core migrations do not support generated/default values for column '{name}'."
            );
        }

        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name));
        builder.Append(" ").Append(GetStarRocksColumnType(schema, table, name, operation, model));
        builder.Append(operation.IsNullable ? " NULL" : " NOT NULL");
    }

    protected override void Generate(
        AddColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        throw CreateUnsupportedMigrationOperationException("ADD COLUMN");
    }

    protected override void Generate(
        DropColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        throw CreateUnsupportedMigrationOperationException("DROP COLUMN");
    }

    protected override void Generate(
        AlterColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        throw CreateUnsupportedMigrationOperationException("ALTER COLUMN");
    }

    protected override void Generate(
        RenameTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        throw CreateUnsupportedMigrationOperationException("RENAME TABLE");
    }

    protected override void Generate(
        RenameColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        throw CreateUnsupportedMigrationOperationException("RENAME COLUMN");
    }

    protected override void Generate(
        CreateIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        throw CreateUnsupportedMigrationOperationException("CREATE INDEX");
    }

    protected override void Generate(
        DropIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        throw CreateUnsupportedMigrationOperationException("DROP INDEX");
    }

    protected override void Generate(
        AddPrimaryKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        throw CreateUnsupportedMigrationOperationException("ADD PRIMARY KEY");
    }

    protected override void Generate(
        DropPrimaryKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        throw CreateUnsupportedMigrationOperationException("DROP PRIMARY KEY");
    }

    protected override void Generate(
        AddForeignKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        throw CreateUnsupportedMigrationOperationException("ADD FOREIGN KEY");
    }

    private void AppendDelimitedColumnList(MigrationCommandListBuilder builder, string[] columns)
    {
        for (int i = 0; i < columns.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(columns[i]));
        }
    }

    private string GetStarRocksColumnType(
        string? schema,
        string table,
        string name,
        ColumnOperation operation,
        IModel? model
    )
    {
        string columnType =
            operation.ColumnType ?? GetColumnType(schema, table, name, operation, model);
        if (string.Equals(columnType, "varchar", StringComparison.OrdinalIgnoreCase))
        {
            return operation.MaxLength is { } maxLength
                ? string.Create(CultureInfo.InvariantCulture, $"varchar({maxLength})")
                : "varchar(255)";
        }

        return columnType;
    }

    private static NotSupportedException CreateUnsupportedMigrationOperationException(
        string operation
    ) =>
        new(
            $"DotRocks EF Core migrations do not support {operation}; only conservative CREATE DATABASE, CREATE TABLE, and DROP TABLE operations are supported in this release."
        );

    private static string GetKeyClause(CreateTableOperation operation)
    {
        object? value = operation.FindAnnotation(DotRocksAnnotationNames.KeyModel)?.Value;
        return value switch
        {
            null => "DUPLICATE KEY",
            DotRocksTableKeyModel.DuplicateKey => "DUPLICATE KEY",
            DotRocksTableKeyModel.PrimaryKey => "PRIMARY KEY",
            string text
                when string.Equals(text, "DUPLICATE KEY", StringComparison.OrdinalIgnoreCase) =>
                "DUPLICATE KEY",
            string text
                when string.Equals(text, "PRIMARY KEY", StringComparison.OrdinalIgnoreCase) =>
                "PRIMARY KEY",
            _ => throw new NotSupportedException(
                $"DotRocks EF Core migrations do not support StarRocks table key model '{value}'."
            ),
        };
    }

    private static string[] GetColumnListAnnotation(
        CreateTableOperation operation,
        string annotationName,
        string[] fallback,
        string description
    )
    {
        object? value = operation.FindAnnotation(annotationName)?.Value;
        string[] columns = value switch
        {
            null => fallback,
            string[] stringArray => stringArray,
            IReadOnlyList<string> stringList => stringList.ToArray(),
            _ => throw new NotSupportedException(
                $"DotRocks EF Core migrations require {description} columns to be configured as string column names."
            ),
        };

        if (columns.Length == 0 || columns.Any(string.IsNullOrWhiteSpace))
        {
            throw new NotSupportedException(
                $"DotRocks EF Core migrations require at least one non-empty {description} column."
            );
        }

        return columns;
    }

    private static int GetPositiveIntAnnotation(
        CreateTableOperation operation,
        string annotationName,
        int fallback,
        string description
    )
    {
        object? value = operation.FindAnnotation(annotationName)?.Value;
        int number = value switch
        {
            null => fallback,
            int intValue => intValue,
            _ => throw new NotSupportedException(
                $"DotRocks EF Core migrations require {description} to be configured as a positive integer."
            ),
        };

        if (number <= 0)
        {
            throw new NotSupportedException(
                $"DotRocks EF Core migrations require {description} to be greater than zero."
            );
        }

        return number;
    }

    private static void ValidateColumnsExist(
        CreateTableOperation operation,
        string[] columns,
        string description
    )
    {
        HashSet<string> knownColumns = operation
            .Columns.Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string column in columns)
        {
            if (!knownColumns.Contains(column))
            {
                throw new NotSupportedException(
                    $"DotRocks EF Core migrations cannot use unknown column '{column}' in the StarRocks {description} clause."
                );
            }
        }
    }

    private static void ValidateKeyColumnTypes(CreateTableOperation operation, string[] keyColumns)
    {
        var keyColumnSet = keyColumns.ToHashSet(StringComparer.Ordinal);
        foreach (AddColumnOperation column in operation.Columns)
        {
            if (keyColumnSet.Contains(column.Name) && IsFloatingPointStoreType(column.ColumnType))
            {
                // StarRocks forbids FLOAT/DOUBLE as key columns; reject before emitting DDL.
                throw new NotSupportedException(
                    $"DotRocks EF Core migrations cannot use floating-point column '{column.Name}' "
                        + "in a StarRocks table key; FLOAT and DOUBLE are not allowed as key columns."
                );
            }
        }
    }

    private static bool IsFloatingPointStoreType(string? columnType)
    {
        if (string.IsNullOrEmpty(columnType))
        {
            return false;
        }

        string baseType = columnType.Split('(')[0].Trim();
        return baseType.Equals("float", StringComparison.OrdinalIgnoreCase)
            || baseType.Equals("double", StringComparison.OrdinalIgnoreCase);
    }
}
