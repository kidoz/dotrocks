using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksMigrationsTests
{
    [Fact]
    public void Generate_CreateTable_ProducesStarRocksPrimaryKeyTableSql()
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        string sql = GenerateSql(generator, CreateWidgetsTable());

        Assert.Contains("CREATE TABLE `unit_db`.`widgets`", sql, StringComparison.Ordinal);
        Assert.Contains("`id` int NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("`name` varchar(64) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("DUPLICATE KEY(`id`)", sql, StringComparison.Ordinal);
        Assert.Contains("DISTRIBUTED BY HASH(`id`) BUCKETS 1", sql, StringComparison.Ordinal);
        Assert.Contains("PROPERTIES ('replication_num' = '1')", sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnsupportedOperations))]
    public void Generate_UnsupportedMigrationOperation_ThrowsNotSupportedException(
        MigrationOperation operation,
        string expectedMessage
    )
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            generator.Generate([operation], context.Model)
        );

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_CreateTableWithDefault_ThrowsNotSupportedException()
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        CreateTableOperation operation = CreateWidgetsTable();
        operation.Columns[1].DefaultValue = "created";

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            generator.Generate([operation], context.Model)
        );

        Assert.Contains("generated/default", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_CreateTableWithComputedColumn_ThrowsNotSupportedException()
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        CreateTableOperation operation = CreateWidgetsTable();
        operation.Columns[1].ComputedColumnSql = "concat(name, '_computed')";

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            generator.Generate([operation], context.Model)
        );

        Assert.Contains("generated/default", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HistoryRepository_GeneratesStarRocksHistorySql()
    {
        using var context = CreateContext();
        var history = context.GetService<IHistoryRepository>();

        string createSql = history.GetCreateIfNotExistsScript();
        string insertSql = history.GetInsertScript(new HistoryRow("202606190001_Create", "10.0.9"));
        string deleteSql = history.GetDeleteScript("202606190001_Create");

        Assert.Contains(
            "CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory`",
            createSql,
            StringComparison.Ordinal
        );
        Assert.Contains("DUPLICATE KEY(`MigrationId`)", createSql, StringComparison.Ordinal);
        Assert.Contains(
            "DISTRIBUTED BY HASH(`MigrationId`) BUCKETS 1",
            createSql,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`) VALUES ('202606190001_Create', '10.0.9')",
            insertSql,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "DELETE FROM `__EFMigrationsHistory` WHERE `MigrationId` = '202606190001_Create'",
            deleteSql,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void HistoryRepository_IdempotentScripts_ThrowNotSupportedException()
    {
        using var context = CreateContext();
        var history = context.GetService<IHistoryRepository>();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            history.GetBeginIfNotExistsScript("202606190001_Create")
        );

        Assert.Contains("idempotent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<MigrationOperation, string> UnsupportedOperations() =>
        new()
        {
            { CreateAddColumnOperation(), "ADD COLUMN" },
            { CreateDropColumnOperation(), "DROP COLUMN" },
            { CreateAlterColumnOperation(), "ALTER COLUMN" },
            { CreateRenameTableOperation(), "RENAME TABLE" },
            { CreateRenameColumnOperation(), "RENAME COLUMN" },
            { CreateIndexOperation(), "CREATE INDEX" },
            { CreateDropIndexOperation(), "DROP INDEX" },
            { CreateAddPrimaryKeyOperation(), "ADD PRIMARY KEY" },
            { CreateDropPrimaryKeyOperation(), "DROP PRIMARY KEY" },
            { CreateForeignKeyOperation(), "ADD FOREIGN KEY" },
        };

    private static string GenerateSql(
        IMigrationsSqlGenerator generator,
        params MigrationOperation[] operations
    ) =>
        string.Concat(
            generator.Generate(operations, model: null).Select(command => command.CommandText)
        );

    private static CreateTableOperation CreateWidgetsTable()
    {
        var operation = new CreateTableOperation { Name = "widgets", Schema = "unit_db" };
        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "id",
                Table = operation.Name,
                Schema = operation.Schema,
                ClrType = typeof(int),
                ColumnType = "int",
                IsNullable = false,
            }
        );
        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "name",
                Table = operation.Name,
                Schema = operation.Schema,
                ClrType = typeof(string),
                ColumnType = "varchar(64)",
                IsNullable = false,
            }
        );
        operation.PrimaryKey = new AddPrimaryKeyOperation
        {
            Name = "PK_widgets",
            Table = operation.Name,
            Schema = operation.Schema,
            Columns = ["id"],
        };
        return operation;
    }

    private static AddColumnOperation CreateAddColumnOperation() =>
        new()
        {
            Name = "name",
            Table = "widgets",
            Schema = "unit_db",
            ClrType = typeof(string),
            ColumnType = "varchar(64)",
        };

    private static AlterColumnOperation CreateAlterColumnOperation() =>
        new()
        {
            Name = "name",
            Table = "widgets",
            Schema = "unit_db",
            ClrType = typeof(string),
            ColumnType = "varchar(128)",
            OldColumn = new AddColumnOperation
            {
                Name = "name",
                Table = "widgets",
                Schema = "unit_db",
                ClrType = typeof(string),
                ColumnType = "varchar(64)",
            },
        };

    private static DropColumnOperation CreateDropColumnOperation() =>
        new()
        {
            Name = "name",
            Table = "widgets",
            Schema = "unit_db",
        };

    private static RenameTableOperation CreateRenameTableOperation() =>
        new()
        {
            Name = "widgets",
            Schema = "unit_db",
            NewName = "widgets_renamed",
            NewSchema = "unit_db",
        };

    private static RenameColumnOperation CreateRenameColumnOperation() =>
        new()
        {
            Name = "name",
            Table = "widgets",
            Schema = "unit_db",
            NewName = "display_name",
        };

    private static CreateIndexOperation CreateIndexOperation() =>
        new()
        {
            Name = "IX_widgets_name",
            Table = "widgets",
            Schema = "unit_db",
            Columns = ["name"],
        };

    private static DropIndexOperation CreateDropIndexOperation() =>
        new()
        {
            Name = "IX_widgets_name",
            Table = "widgets",
            Schema = "unit_db",
        };

    private static AddPrimaryKeyOperation CreateAddPrimaryKeyOperation() =>
        new()
        {
            Name = "PK_widgets",
            Table = "widgets",
            Schema = "unit_db",
            Columns = ["id"],
        };

    private static DropPrimaryKeyOperation CreateDropPrimaryKeyOperation() =>
        new()
        {
            Name = "PK_widgets",
            Table = "widgets",
            Schema = "unit_db",
        };

    private static AddForeignKeyOperation CreateForeignKeyOperation() =>
        new()
        {
            Name = "FK_widgets_parent",
            Table = "widgets",
            Schema = "unit_db",
            Columns = ["parent_id"],
            PrincipalTable = "parents",
            PrincipalSchema = "unit_db",
            PrincipalColumns = ["id"],
        };

    private static UnitContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<UnitContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");
        return new UnitContext(optionsBuilder.Options);
    }

    private sealed class UnitContext(DbContextOptions<UnitContext> options) : DbContext(options)
    {
        public DbSet<UnitWidget> Widgets => Set<UnitWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .Entity<UnitWidget>()
                .ToTable("widgets", "unit_db")
                .HasKey(widget => widget.Id);
            modelBuilder.Entity<UnitWidget>().Property(widget => widget.Id).ValueGeneratedNever();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class UnitWidget
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
