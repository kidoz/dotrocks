using DotRocks.EntityFrameworkCore.Design;
using DotRocks.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksMigrationsTests
{
    private static readonly string[] IdColumn = ["id"];
    private static readonly string[] NameColumn = ["name"];
    private static readonly string[] MissingColumn = ["missing"];

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

    [Fact]
    public void Generate_CreateTableWithPrimaryKeyShape_ProducesPrimaryKeyTableSql()
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        CreateTableOperation operation = CreateWidgetsTable();
        operation.AddAnnotation("DotRocks:KeyModel", DotRocksTableKeyModel.PrimaryKey);
        operation.AddAnnotation("DotRocks:KeyColumns", IdColumn);

        string sql = GenerateSql(generator, operation);

        Assert.Contains("PRIMARY KEY(`id`)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DUPLICATE KEY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_CreateTableWithUniqueKeyShape_ProducesUniqueKeyTableSql()
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        CreateTableOperation operation = CreateWidgetsTable();
        operation.AddAnnotation("DotRocks:KeyModel", DotRocksTableKeyModel.UniqueKey);
        operation.AddAnnotation("DotRocks:KeyColumns", IdColumn);

        string sql = GenerateSql(generator, operation);

        Assert.Contains("UNIQUE KEY(`id`)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DUPLICATE KEY", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIMARY KEY(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_CreateTableWithFloatingPointKeyColumn_Throws()
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var operation = new CreateTableOperation { Name = "metrics", Schema = "unit_db" };
        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "ratio",
                Table = operation.Name,
                Schema = operation.Schema,
                ClrType = typeof(double),
                ColumnType = "double",
                IsNullable = false,
            }
        );
        operation.PrimaryKey = new AddPrimaryKeyOperation
        {
            Name = "PK_metrics",
            Table = operation.Name,
            Schema = operation.Schema,
            Columns = ["ratio"],
        };

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            GenerateSql(generator, operation)
        );
        Assert.Contains("FLOAT and DOUBLE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_CreateTableWithDistributionAndReplicationShape_UsesConfiguredValues()
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        CreateTableOperation operation = CreateWidgetsTable();
        operation.AddAnnotation("DotRocks:DistributionColumns", NameColumn);
        operation.AddAnnotation("DotRocks:DistributionBuckets", 8);
        operation.AddAnnotation("DotRocks:ReplicationNum", 3);

        string sql = GenerateSql(generator, operation);

        Assert.Contains("DISTRIBUTED BY HASH(`name`) BUCKETS 8", sql, StringComparison.Ordinal);
        Assert.Contains("PROPERTIES ('replication_num' = '3')", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_EnsureSchema_ProducesCreateDatabaseIfNotExistsSql()
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        string sql = GenerateSql(generator, new EnsureSchemaOperation { Name = "unit_db" });

        Assert.Contains("CREATE DATABASE IF NOT EXISTS `unit_db`", sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnsupportedTableShapeAnnotations))]
    public void Generate_CreateTableWithUnsupportedTableShapeAnnotation_ThrowsNotSupportedException(
        string annotation,
        object value,
        string expectedMessage
    )
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        CreateTableOperation operation = CreateWidgetsTable();
        operation.AddAnnotation(annotation, value);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            GenerateSql(generator, operation)
        );

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EntityTypeBuilderExtensions_SetStarRocksTableShapeAnnotations()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TableShapeContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");

        using var context = new TableShapeContext(optionsBuilder.Options);
        var entityType = context.Model.FindEntityType(typeof(TableShapeWidget));

        Assert.NotNull(entityType);
        Assert.Equal(
            DotRocksTableKeyModel.PrimaryKey,
            entityType.FindAnnotation("DotRocks:KeyModel")?.Value
        );
        Assert.Equal(
            IdColumn,
            Assert.IsType<string[]>(entityType.FindAnnotation("DotRocks:KeyColumns")?.Value)
        );
        Assert.Equal(
            IdColumn,
            Assert.IsType<string[]>(
                entityType.FindAnnotation("DotRocks:DistributionColumns")?.Value
            )
        );
        Assert.Equal(4, entityType.FindAnnotation("DotRocks:DistributionBuckets")?.Value);
        Assert.Equal(2, entityType.FindAnnotation("DotRocks:ReplicationNum")?.Value);
    }

    [Fact]
    public void MigrationsModelDiffer_CarriesTableShapeAnnotationsFromModel()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TableShapeContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");
        using var context = new TableShapeContext(optionsBuilder.Options);
        var differ = context.GetService<IMigrationsModelDiffer>();
        IRelationalModel model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        CreateTableOperation operation = Assert.Single(
            differ.GetDifferences(null, model).OfType<CreateTableOperation>()
        );

        Assert.Equal(
            DotRocksTableKeyModel.PrimaryKey,
            operation.FindAnnotation("DotRocks:KeyModel")?.Value
        );
        Assert.Equal(
            IdColumn,
            Assert.IsType<string[]>(operation.FindAnnotation("DotRocks:KeyColumns")?.Value)
        );
        Assert.Equal(
            IdColumn,
            Assert.IsType<string[]>(operation.FindAnnotation("DotRocks:DistributionColumns")?.Value)
        );
        Assert.Equal(4, operation.FindAnnotation("DotRocks:DistributionBuckets")?.Value);
        Assert.Equal(2, operation.FindAnnotation("DotRocks:ReplicationNum")?.Value);
    }

    [Fact]
    public void CSharpMigrationGenerator_PreservesTableShapeAnnotationsInGeneratedCode()
    {
        using var context = CreateTableShapeContext();
        var differ = context.GetService<IMigrationsModelDiffer>();
        IRelationalModel model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        CreateTableOperation operation = Assert.Single(
            differ.GetDifferences(null, model).OfType<CreateTableOperation>()
        );
        IMigrationsCodeGenerator generator = SelectCSharpMigrationsGenerator();

        string migration = generator.GenerateMigration(
            "DotRocks.EntityFrameworkCore.Tests.Migrations",
            "CreateTableShape",
            [operation],
            []
        );
        string snapshot = generator.GenerateSnapshot(
            "DotRocks.EntityFrameworkCore.Tests.Migrations",
            typeof(TableShapeContext),
            "TableShapeContextModelSnapshot",
            context.GetService<IDesignTimeModel>().Model
        );

        Assert.Contains(
            ".Annotation(\"DotRocks:KeyModel\", DotRocksTableKeyModel.PrimaryKey)",
            migration,
            StringComparison.Ordinal
        );
        Assert.Contains(
            ".Annotation(\"DotRocks:KeyColumns\", new[] { \"id\" })",
            migration,
            StringComparison.Ordinal
        );
        Assert.Contains(
            ".Annotation(\"DotRocks:DistributionColumns\", new[] { \"id\" })",
            migration,
            StringComparison.Ordinal
        );
        Assert.Contains(
            ".Annotation(\"DotRocks:DistributionBuckets\", 4)",
            migration,
            StringComparison.Ordinal
        );
        Assert.Contains(
            ".Annotation(\"DotRocks:ReplicationNum\", 2)",
            migration,
            StringComparison.Ordinal
        );
        Assert.Contains(".HasStarRocksPrimaryKey(\"id\")", snapshot, StringComparison.Ordinal);
        Assert.Contains(
            ".HasStarRocksHashDistribution(4, \"id\")",
            snapshot,
            StringComparison.Ordinal
        );
        Assert.Contains(".HasStarRocksReplicationNum(2)", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityTypeBuilderExtensions_RejectInvalidTableShapeValues()
    {
        var builder = new ModelBuilder();

        Assert.Throws<ArgumentException>(() =>
            builder.Entity<TableShapeWidget>().HasStarRocksPrimaryKey([])
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.Entity<TableShapeWidget>().HasStarRocksHashDistribution(0, "id")
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.Entity<TableShapeWidget>().HasStarRocksReplicationNum(0)
        );
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
            {
                new DropSchemaOperation { Name = "unit_db" },
                "DROP DATABASE"
            },
            {
                new SqlOperation { Sql = "TRUNCATE TABLE `widgets`" },
                "TRUNCATE TABLE"
            },
        };

    public static TheoryData<string, object, string> UnsupportedTableShapeAnnotations() =>
        new()
        {
            { "DotRocks:KeyModel", "AGGREGATE KEY", "table key model" },
            { "DotRocks:KeyColumns", Array.Empty<string>(), "table key column" },
            { "DotRocks:KeyColumns", MissingColumn, "unknown column" },
            { "DotRocks:DistributionColumns", MissingColumn, "unknown column" },
            { "DotRocks:DistributionBuckets", 0, "bucket" },
            { "DotRocks:ReplicationNum", 0, "replication" },
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

    private static TableShapeContext CreateTableShapeContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TableShapeContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");
        return new TableShapeContext(optionsBuilder.Options);
    }

    private static IMigrationsCodeGenerator SelectCSharpMigrationsGenerator()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        new DotRocksDesignTimeServices().ConfigureDesignTimeServices(services);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMigrationsCodeGeneratorSelector>().Select("C#");
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

    private sealed class TableShapeContext(DbContextOptions<TableShapeContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TableShapeWidget>(entity =>
            {
                entity.ToTable("table_shape_widgets", "unit_db");
                entity.HasKey(widget => widget.Id);
                entity.Property(widget => widget.Id).ValueGeneratedNever().HasColumnName("id");
                entity.HasStarRocksPrimaryKey("id");
                entity.HasStarRocksHashDistribution(4, "id");
                entity.HasStarRocksReplicationNum(2);
            });
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbContext model metadata."
    )]
    private sealed class TableShapeWidget
    {
        public int Id { get; set; }
    }
}
