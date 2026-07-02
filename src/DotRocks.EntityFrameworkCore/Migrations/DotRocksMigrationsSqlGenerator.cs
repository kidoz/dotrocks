using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotRocks.EntityFrameworkCore.Metadata;
using DotRocks.EntityFrameworkCore.Storage;
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

        TableShape shape = TableShape.FromOperation(operation);
        ValidateTableShape(operation, shape);

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

        builder.Append(") ").Append(DotRocksTableKeyModels.ToKeyClause(shape.KeyModel)).Append("(");
        AppendDelimitedColumnList(builder, shape.KeyColumns);
        builder.AppendLine(")");
        if (shape.RandomDistribution)
        {
            builder.Append("DISTRIBUTED BY RANDOM BUCKETS ");
        }
        else
        {
            builder.Append("DISTRIBUTED BY HASH(");
            AppendDelimitedColumnList(builder, shape.DistributionColumns);
            builder.Append(") BUCKETS ");
        }

        builder
            .Append(shape.DistributionBuckets.ToString(CultureInfo.InvariantCulture))
            .AppendLine();

        if (shape.SortKeyColumns.Length > 0)
        {
            builder.Append("ORDER BY (");
            AppendDelimitedColumnList(builder, shape.SortKeyColumns);
            builder.AppendLine(")");
        }

        AppendProperties(builder, shape.Properties);

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

    /// <summary>
    /// The parsed StarRocks table shape of one CREATE TABLE operation. Annotation reading,
    /// coercion, and defaulting happen in <see cref="FromOperation"/>, so the emit path in
    /// <see cref="Generate(CreateTableOperation, IModel?, MigrationCommandListBuilder, bool)"/>
    /// reads linearly and a new table option plugs into this one seam.
    /// </summary>
    private sealed record TableShape(
        DotRocksTableKeyModel KeyModel,
        string[] KeyColumns,
        bool RandomDistribution,
        string[] DistributionColumns,
        int DistributionBuckets,
        string[] SortKeyColumns,
        SortedDictionary<string, string> Properties
    )
    {
        public static TableShape FromOperation(CreateTableOperation operation)
        {
            DotRocksTableKeyModel keyModel =
                DotRocksTableShapeAnnotations.CoerceKeyModel(
                    operation.FindAnnotation(DotRocksAnnotationNames.KeyModel)?.Value
                ) ?? DotRocksTableKeyModel.DuplicateKey;
            string[] keyColumns =
                DotRocksTableShapeAnnotations.CoerceColumnList(
                    operation.FindAnnotation(DotRocksAnnotationNames.KeyColumns)?.Value,
                    "table key"
                ) ?? operation.PrimaryKey!.Columns;
            bool randomDistribution = DotRocksTableShapeAnnotations.CoerceFlag(
                operation.FindAnnotation(DotRocksAnnotationNames.RandomDistribution)?.Value,
                "random distribution"
            );
            string[] distributionColumns = randomDistribution
                ? []
                : DotRocksTableShapeAnnotations.CoerceColumnList(
                    operation.FindAnnotation(DotRocksAnnotationNames.DistributionColumns)?.Value,
                    "hash distribution"
                ) ?? keyColumns;
            int distributionBuckets =
                DotRocksTableShapeAnnotations.CoercePositiveInt(
                    operation.FindAnnotation(DotRocksAnnotationNames.DistributionBuckets)?.Value,
                    "distribution bucket count"
                ) ?? 1;
            string[] sortKeyColumns =
                DotRocksTableShapeAnnotations.CoerceColumnList(
                    operation.FindAnnotation(DotRocksAnnotationNames.SortKeyColumns)?.Value,
                    "sort key"
                ) ?? [];
            int replicationNum =
                DotRocksTableShapeAnnotations.CoercePositiveInt(
                    operation.FindAnnotation(DotRocksAnnotationNames.ReplicationNum)?.Value,
                    "replication number"
                ) ?? 1;

            return new TableShape(
                keyModel,
                keyColumns,
                randomDistribution,
                distributionColumns,
                distributionBuckets,
                sortKeyColumns,
                BuildProperties(operation, replicationNum)
            );
        }

        private static SortedDictionary<string, string> BuildProperties(
            CreateTableOperation operation,
            int replicationNum
        )
        {
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["replication_num"] = replicationNum.ToString(CultureInfo.InvariantCulture),
            };

            IReadOnlyDictionary<string, string>? custom =
                DotRocksTableShapeAnnotations.CoercePropertyMap(
                    operation.FindAnnotation(DotRocksAnnotationNames.Properties)?.Value
                );
            if (custom is not null)
            {
                foreach (KeyValuePair<string, string> entry in custom)
                {
                    // replication_num is taken from HasStarRocksReplicationNum; a duplicate is ignored.
                    if (!string.Equals(entry.Key, "replication_num", StringComparison.Ordinal))
                    {
                        properties[entry.Key] = entry.Value;
                    }
                }
            }

            return properties;
        }
    }

    private static void ValidateTableShape(CreateTableOperation operation, TableShape shape)
    {
        HashSet<string> knownColumns = operation
            .Columns.Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        ValidateColumnsExist(knownColumns, shape.KeyColumns, "table key");
        ValidateColumnsExist(knownColumns, shape.SortKeyColumns, "sort key");
        ValidateColumnsExist(knownColumns, shape.DistributionColumns, "hash distribution");
        ValidateKeyColumnTypes(operation, shape.KeyColumns);
    }

    private static void AppendProperties(
        MigrationCommandListBuilder builder,
        SortedDictionary<string, string> properties
    )
    {
        builder.Append("PROPERTIES (");
        bool first = true;
        foreach (KeyValuePair<string, string> property in properties)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            // Both name and value are emitted as escaped StarRocks string literals so a quote,
            // backslash, or control character cannot break out of or corrupt the literal.
            builder
                .Append(DotRocksStringLiteral.Generate(property.Key))
                .Append(" = ")
                .Append(DotRocksStringLiteral.Generate(property.Value));
        }

        builder.Append(")");
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

    private static void ValidateColumnsExist(
        HashSet<string> knownColumns,
        string[] columns,
        string description
    )
    {
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
