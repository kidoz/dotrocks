using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

        builder.Append(") DUPLICATE KEY(");
        AppendDelimitedColumnList(builder, operation.PrimaryKey.Columns);
        builder.AppendLine(")");
        builder.Append("DISTRIBUTED BY HASH(");
        AppendDelimitedColumnList(builder, operation.PrimaryKey.Columns);
        builder.AppendLine(") BUCKETS 1");
        builder.Append("PROPERTIES ('replication_num' = '1')");

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
            $"DotRocks EF Core migrations do not support {operation}; only initial CREATE TABLE migrations are supported in this release."
        );
}
