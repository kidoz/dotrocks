using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotRocks.Data;
using DotRocks.EntityFrameworkCore.Infrastructure;
using DotRocks.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace DotRocks.EntityFrameworkCore.IntegrationTests;

public sealed class DotRocksEfCoreIntegrationTests
{
    private const string LinqDatabaseName = "dotrocks_ef_core_test";
    private const string WidgetTableName = "widgets";
    private const string WriteWidgetTableName = "ef_write_widgets";
    private const string MigrationWidgetTableName = "ef_migration_widgets";
    private const string MigrationId = "202606190001_CreateEfMigrationWidget";
    private const string TableShapeMigrationWidgetTableName = "ef_table_shape_migration_widgets";
    private const string TableShapeMigrationId = "202606200001_CreateEfTableShapeMigrationWidget";
    private const string EnsureSchemaMigrationDatabaseName =
        "dotrocks_ef_core_migrate_ensure_schema";
    private const string EnsureSchemaMigrationWidgetTableName = "ef_ensure_schema_widgets";
    private const string EnsureSchemaMigrationId = "202606200002_CreateEnsureSchemaWidget";
    private const string UnsupportedMigrationWidgetTableName = "ef_unsupported_migration_widgets";
    private const string UnsupportedMigrationId = "202606200003_UnsupportedSchemaMutation";
    private const string UnsupportedDownMigrationWidgetTableName = "ef_unsupported_down_widgets";
    private const string UnsupportedDownMigrationId = "202606200004_UnsupportedDownMigration";
    private static readonly string[] IdStoreColumn = ["id"];
    private static readonly string[] NameStoreColumn = ["name"];

    [Fact]
    public void UseStarRocks_ConfiguresProviderName()
    {
        var optionsBuilder = new DbContextOptionsBuilder<DotRocksTestContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");

        using var context = new DotRocksTestContext(optionsBuilder.Options);

        Assert.Equal("DotRocks.EntityFrameworkCore", context.Database.ProviderName);
    }

    [Fact]
    public async Task UseStarRocks_GetDbConnection_ExecutesSelectOne()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        var optionsBuilder = new DbContextOptionsBuilder<DotRocksTestContext>();
        optionsBuilder.UseStarRocks(IntegrationTestEnvironment.ConnectionString);

        using var context = new DotRocksTestContext(optionsBuilder.Options);
        DbConnection connection = context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // StarRocks infers the type of a bare integer literal per version: 3.3/4.0 return INT,
        // 3.5 returns BIGINT. The driver faithfully maps the wire type, so compare numerically.
        Assert.Equal(1L, Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task DatabaseCreator_Exists_ReflectsDatabasePresence()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using DotRocksTestContext setup = CreateContext(
            IntegrationTestEnvironment.ConnectionString
        );
        await EnsureLinqDatabaseAsync(setup).ConfigureAwait(true);

        using DotRocksTestContext existing = CreateContext(
            BuildDatabaseConnectionString(LinqDatabaseName)
        );
        var existingCreator = existing.GetService<IRelationalDatabaseCreator>();
        Assert.True(
            await existingCreator
                .ExistsAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );

        // StarRocks refuses login when the default database does not exist, so a connection
        // scoped to a missing database neither connects nor "exists".
        using DotRocksTestContext missing = CreateContext(
            BuildDatabaseConnectionString("dotrocks_definitely_absent_db")
        );
        var missingCreator = missing.GetService<IRelationalDatabaseCreator>();
        Assert.False(
            await missingCreator
                .ExistsAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact]
    public async Task ExecuteSqlRawAsync_CreatesDatabase()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        string databaseName = CreateUniqueDatabaseName();
        await using var context = CreateLiveContext();
        string createSql = "CREATE DATABASE IF NOT EXISTS " + DelimitIdentifier(databaseName);
        string dropSql = "DROP DATABASE IF EXISTS " + DelimitIdentifier(databaseName);

        try
        {
            await context
                .Database.ExecuteSqlRawAsync(createSql, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            await context
                .Database.ExecuteSqlRawAsync(dropSql, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ExecuteSqlRawAsync_DropsTable()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureLinqDatabaseAsync(context).ConfigureAwait(true);
        string dropSql =
            "DROP TABLE IF EXISTS "
            + DelimitIdentifier(LinqDatabaseName)
            + "."
            + DelimitIdentifier("drop_target");

        await context
            .Database.ExecuteSqlRawAsync(dropSql, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The verification query is assembled only from fixed sanitized identifiers and constants."
    )]
    public async Task ExecuteSqlRawAsync_ExecutesParameterizedCommand()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            string insertSql =
                "INSERT INTO "
                + DelimitedWidgetTable()
                + " (id, category, priority, big_value, name, active, created_at, amount, optional_score) "
                + "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8})";
            await context
                .Database.ExecuteSqlRawAsync(
                    insertSql,
                    [
                        42,
                        9,
                        1,
                        10000000042L,
                        "parameterized",
                        true,
                        new DateTime(2026, 6, 19, 14, 15, 16),
                        42.42m,
                        42,
                    ],
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            DbConnection connection = context.Database.GetDbConnection();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = CreateParameterizedVerificationSql();
            if (connection.State != ConnectionState.Open)
            {
                await connection
                    .OpenAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            }

            object? value = await command
                .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(1, Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DateAndMathTranslators_ExecuteAgainstStarRocks()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            // Seeded widgets all have CreatedAt 2026-06-19 at hours 10, 11, 12; amounts 12.34/23.45/34.56.
            Assert.Equal(
                3,
                await context
                    .Widgets.CountAsync(
                        widget => widget.CreatedAt.Year == 2026,
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            );

            List<int> hourIds = await context
                .Widgets.Where(widget => widget.CreatedAt.Hour == 10)
                .Select(widget => widget.Id)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal([1], hourIds);

            Assert.Equal(
                2,
                await context
                    .Widgets.CountAsync(
                        widget => Math.Abs(widget.Amount) > 20m,
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            );

            Assert.Equal(
                3,
                await context
                    .Widgets.CountAsync(
                        widget => widget.CreatedAt.AddDays(1).Day == 20,
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            );
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "EF1003:Method inserts concatenated strings directly into the SQL",
        Justification = "The cleanup and generated DDL is assembled only from fixed sanitized identifiers."
    )]
    public async Task AdvancedTableShape_RandomDistributionSortKeyAndProperties_CreatesTable()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        const string tableName = "ef_advanced_shape";
        await using var context = CreateLiveContext();
        await EnsureLinqDatabaseAsync(context).ConfigureAwait(true);
        await context
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier(tableName),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        var operation = new CreateTableOperation { Name = tableName, Schema = LinqDatabaseName };
        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "id",
                Table = tableName,
                Schema = LinqDatabaseName,
                ClrType = typeof(int),
                ColumnType = "int",
                IsNullable = false,
            }
        );
        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "name",
                Table = tableName,
                Schema = LinqDatabaseName,
                ClrType = typeof(string),
                ColumnType = "varchar(64)",
                IsNullable = false,
            }
        );
        operation.PrimaryKey = new AddPrimaryKeyOperation { Columns = IdStoreColumn };
        operation.AddAnnotation("DotRocks:KeyModel", DotRocksTableKeyModel.DuplicateKey);
        operation.AddAnnotation("DotRocks:KeyColumns", IdStoreColumn);
        operation.AddAnnotation("DotRocks:RandomDistribution", true);
        operation.AddAnnotation("DotRocks:DistributionBuckets", 3);
        operation.AddAnnotation("DotRocks:SortKeyColumns", NameStoreColumn);
        operation.AddAnnotation(
            "DotRocks:Properties",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bloom_filter_columns"] = "name",
            }
        );

        var generator = context.GetService<IMigrationsSqlGenerator>();
        try
        {
            foreach (
                Microsoft.EntityFrameworkCore.Migrations.MigrationCommand command in generator.Generate(
                    [operation],
                    model: null
                )
            )
            {
                await context
                    .Database.ExecuteSqlRawAsync(
                        command.CommandText,
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true);
            }

            Assert.True(
                await TableExistsAsync(context, LinqDatabaseName, tableName).ConfigureAwait(true)
            );
        }
        finally
        {
            await context
                .Database.ExecuteSqlRawAsync(
                    "DROP TABLE IF EXISTS "
                        + DelimitIdentifier(LinqDatabaseName)
                        + "."
                        + DelimitIdentifier(tableName),
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DbSet_MaterializesCommonStarRocksTypes()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            DotRocksWidget widget = await context
                .Widgets.SingleAsync(
                    widget => widget.Id == 1,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            Assert.Equal(1, widget.Category);
            Assert.Equal(2, widget.Priority);
            Assert.Equal(10000000001L, widget.BigValue);
            Assert.Equal("one", widget.Name);
            Assert.True(widget.Active);
            Assert.Equal(new DateTime(2026, 6, 19, 10, 0, 0), widget.CreatedAt);
            Assert.Equal(12.34m, widget.Amount);
            Assert.Null(widget.OptionalScore);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task FromSqlRaw_MaterializesHighPrecisionDecimalAsDotRocksDecimal()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();

        DotRocksDecimalRow row = await context
            .DecimalRows.FromSqlRaw(
                "SELECT CAST('1234567890123456789012345678901234.9000' AS DECIMAL(38, 4)) AS value"
            )
            .SingleAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(DotRocksDecimal.Parse("1234567890123456789012345678901234.9000"), row.Value);
    }

    [Fact]
    public async Task FromSqlRaw_ProjectingHighPrecisionDecimalToDecimalThrowsPrecisionLoss()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();

        await Assert.ThrowsAsync<DotRocksPrecisionLossException>(() =>
            context
                .DecimalOverflowRows.FromSqlRaw(
                    "SELECT CAST('1234567890123456789012345678901234.9000' AS DECIMAL(38, 4)) AS value"
                )
                .SingleAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task FromSqlRaw_MaterializesDateOnlyTimeOnlyGuidAndJson()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();

        ExtraTypeRow row = await context
            .ExtraTypeRows.FromSqlRaw(
                """
                SELECT
                    CAST('2026-06-19' AS DATE) AS date_value,
                    '13:14:15' AS time_value,
                    '9f4f591e-3db2-4879-856c-1c54b4241b76' AS guid_value,
                    CAST('{{"kind":"alpha","n":1}}' AS JSON) AS json_value
                """
            )
            .SingleAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(new DateOnly(2026, 6, 19), row.DateValue);
        Assert.Equal(new TimeOnly(13, 14, 15), row.TimeValue);
        Assert.Equal(Guid.Parse("9f4f591e-3db2-4879-856c-1c54b4241b76"), row.GuidValue);
        Assert.Equal("""{"kind": "alpha", "n": 1}""", row.JsonValue);
    }

    [Fact]
    public async Task FromSqlRaw_MaterializesLargeIntAsInt128()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();

        List<LargeIntRow> rows = await context
            .LargeIntRows.FromSqlRaw(
                """
                SELECT CAST('170141183460469231731687303715884105727' AS LARGEINT) AS value
                UNION ALL
                SELECT CAST('-170141183460469231731687303715884105728' AS LARGEINT) AS value
                """
            )
            .ToListAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(
            Int128.Parse("170141183460469231731687303715884105727", CultureInfo.InvariantCulture),
            rows[0].Value
        );
        Assert.Equal(
            Int128.Parse("-170141183460469231731687303715884105728", CultureInfo.InvariantCulture),
            rows[1].Value
        );
    }

    [Fact]
    public async Task GroupBy_Category_AggregatesAgainstStarRocks()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            var groups = await context
                .Widgets.GroupBy(widget => widget.Category)
                .Select(group => new
                {
                    Category = group.Key,
                    Count = group.LongCount(),
                    Total = group.Sum(widget => widget.Id),
                })
                .OrderBy(row => row.Category)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            // Seeded widgets: category 1 -> ids {1, 2}, category 2 -> id {3}.
            Assert.Equal(2, groups.Count);
            Assert.Equal(1, groups[0].Category);
            Assert.Equal(2, groups[0].Count);
            Assert.Equal(3, groups[0].Total);
            Assert.Equal(2, groups[1].Category);
            Assert.Equal(1, groups[1].Count);
            Assert.Equal(3, groups[1].Total);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task GroupBy_WithHaving_FiltersAggregatesAgainstStarRocks()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            List<int> categories = await context
                .Widgets.GroupBy(widget => widget.Category)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            // Only category 1 has more than one widget.
            Assert.Equal([1], categories);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Join_SelfJoinOnPriority_ExecutesAgainstStarRocks()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            var pairs = await context
                .Widgets.Join(
                    context.Widgets,
                    widget => widget.Priority,
                    other => other.Id,
                    (widget, other) => new { Left = widget.Id, Right = other.Id }
                )
                .OrderBy(pair => pair.Left)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            // priority(id1)=2 -> other id 2; priority(id2)=1 -> id 1; priority(id3)=1 -> id 1.
            Assert.Equal(3, pairs.Count);
            Assert.Equal(2, pairs.Single(pair => pair.Left == 1).Right);
            Assert.Equal(1, pairs.Single(pair => pair.Left == 2).Right);
            Assert.Equal(1, pairs.Single(pair => pair.Left == 3).Right);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DetectAsync_ReadsLiveServerVersion()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        StarRocksServerVersion version = await StarRocksServerVersion
            .DetectAsync(
                IntegrationTestEnvironment.ConnectionString,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        // The pinned test image is StarRocks 3.x/4.x; only assert a sane lower bound.
        Assert.True(version.Major >= 3, $"Unexpected detected StarRocks version: {version}");
    }

    [Fact]
    public void BinaryClrType_RemainsUnsupportedForEfMapping()
    {
        using DotRocksTestContext context = CreateContext(
            "Server=127.0.0.1;Port=9030;User ID=root"
        );
        var source = context.GetService<IRelationalTypeMappingSource>();

        Assert.Null(source.FindMapping("varbinary"));
    }

    [Fact]
    public void EnsureCreated_ThrowsNotSupportedException()
    {
        using var context = CreateContext("Server=127.0.0.1;Port=9030;User ID=root");

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            context.Database.EnsureCreated()
        );
        Assert.Contains("schema creation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveChangesAsync_InsertsUpdatesAndDeletesSupportedEntity()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        var interceptor = new CapturingCommandInterceptor();
        await using var context = CreateLiveContext(interceptor);
        await EnsureWriteWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            var row = new EfWriteWidget
            {
                Id = 10,
                Name = "inserted",
                Active = true,
                Amount = 12.34m,
            };
            context.WriteWidgets.Add(row);
            interceptor.Clear();
            Assert.Equal(
                1,
                await context
                    .SaveChangesAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
            CapturedCommand insertCommand = interceptor.SingleNonQueryCommand("INSERT");
            Assert.Contains("@p", insertCommand.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("inserted", insertCommand.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("12.34", insertCommand.CommandText, StringComparison.Ordinal);
            Assert.Contains("inserted", insertCommand.ParameterValues());
            Assert.Contains(12.34m, insertCommand.ParameterValues());

            EfWriteWidget inserted = await context
                .WriteWidgets.AsNoTracking()
                .SingleAsync(widget => widget.Id == 10, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal("inserted", inserted.Name);
            Assert.True(inserted.Active);

            row.Name = "updated";
            row.Active = false;
            row.Amount = 56.78m;
            interceptor.Clear();
            Assert.Equal(
                1,
                await context
                    .SaveChangesAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
            CapturedCommand updateCommand = interceptor.SingleNonQueryCommand("UPDATE");
            Assert.Contains(" WHERE ", updateCommand.CommandText, StringComparison.Ordinal);
            Assert.Contains("@p", updateCommand.CommandText, StringComparison.Ordinal);
            // The key column name matches case-insensitively (`Id` model property → `id` column).
            Assert.Contains(
                "`id` = @p",
                updateCommand.CommandText,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.DoesNotContain("updated", updateCommand.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("56.78", updateCommand.CommandText, StringComparison.Ordinal);
            Assert.Contains("updated", updateCommand.ParameterValues());
            Assert.Contains(56.78m, updateCommand.ParameterValues());

            EfWriteWidget updated = await context
                .WriteWidgets.AsNoTracking()
                .SingleAsync(widget => widget.Id == 10, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal("updated", updated.Name);
            Assert.False(updated.Active);
            Assert.Equal(56.78m, updated.Amount);

            context.WriteWidgets.Remove(row);
            interceptor.Clear();
            Assert.Equal(
                1,
                await context
                    .SaveChangesAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
            CapturedCommand deleteCommand = interceptor.SingleNonQueryCommand("DELETE");
            Assert.Contains(" WHERE ", deleteCommand.CommandText, StringComparison.Ordinal);
            Assert.Contains("@p", deleteCommand.CommandText, StringComparison.Ordinal);
            Assert.Contains(
                "`id` = @p",
                deleteCommand.CommandText,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.DoesNotContain("10", deleteCommand.CommandText, StringComparison.Ordinal);
            Assert.Contains(10, deleteCommand.ParameterValues());

            Assert.Equal(
                0,
                await context
                    .WriteWidgets.CountAsync(
                        widget => widget.Id == 10,
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            );
        }
        finally
        {
            await DropWriteWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_MultipleRowsSameTable_CharacterizesStarRocksMultiDml()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWriteWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            // Multiple changed rows for the same table make EF wrap several DML statements in one
            // transaction. This is version-dependent on StarRocks: newer builds (4.0.x) reject a
            // second DML against a table already written in the same transaction (error 5303),
            // while older builds (3.3.x) accept it. Characterize both outcomes; for portable bulk
            // writes use one row per SaveChanges (separate transactions) or Stream Load.
            context.WriteWidgets.AddRange(
                new EfWriteWidget
                {
                    Id = 20,
                    Name = "first",
                    Active = true,
                    Amount = 20.01m,
                },
                new EfWriteWidget
                {
                    Id = 21,
                    Name = "second",
                    Active = false,
                    Amount = 21.02m,
                }
            );

            try
            {
                int affected = await context
                    .SaveChangesAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);

                // Accepted (older StarRocks): both rows are written.
                Assert.Equal(2, affected);
                int count = await context
                    .WriteWidgets.AsNoTracking()
                    .CountAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Assert.Equal(2, count);
            }
            catch (DbUpdateException ex) when (ex.InnerException is DotRocksException inner)
            {
                // Rejected (newer StarRocks): the multi-DML-in-one-transaction limitation.
                Assert.Contains(
                    "subjected to DML operations before",
                    inner.Message,
                    StringComparison.OrdinalIgnoreCase
                );
            }
        }
        finally
        {
            await DropWriteWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_OneRowPerSaveChanges_WritesSequentially()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        var interceptor = new CapturingCommandInterceptor();
        await using var context = CreateLiveContext(interceptor);
        await EnsureWriteWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            // One row per SaveChanges keeps each DML in its own implicit transaction, which is the
            // supported StarRocks write pattern.
            foreach (
                EfWriteWidget widget in new[]
                {
                    new EfWriteWidget
                    {
                        Id = 20,
                        Name = "first",
                        Active = true,
                        Amount = 20.01m,
                    },
                    new EfWriteWidget
                    {
                        Id = 21,
                        Name = "second",
                        Active = false,
                        Amount = 21.02m,
                    },
                }
            )
            {
                context.WriteWidgets.Add(widget);
                interceptor.Clear();
                Assert.Equal(
                    1,
                    await context
                        .SaveChangesAsync(TestContext.Current.CancellationToken)
                        .ConfigureAwait(true)
                );
                Assert.All(interceptor.NonQueryCommands("INSERT"), AssertParameterizedDml);
            }

            List<int> ids = await context
                .WriteWidgets.AsNoTracking()
                .OrderBy(widget => widget.Id)
                .Select(widget => widget.Id)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal([20, 21], ids);
        }
        finally
        {
            await DropWriteWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_InsideEfTransactionCommitPersistsRows()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWriteWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            using IDbContextTransaction transaction = await context
                .Database.BeginTransactionAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            context.WriteWidgets.Add(
                new EfWriteWidget
                {
                    Id = 30,
                    Name = "committed",
                    Active = true,
                    Amount = 30.30m,
                }
            );

            Assert.Equal(
                1,
                await context
                    .SaveChangesAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
            await transaction
                .CommitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(
                1,
                await context
                    .WriteWidgets.AsNoTracking()
                    .CountAsync(widget => widget.Id == 30, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
        }
        finally
        {
            await DropWriteWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_InsideEfTransactionRollbackHidesRowsWhenSupported()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWriteWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            using IDbContextTransaction transaction = await context
                .Database.BeginTransactionAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            context.WriteWidgets.Add(
                new EfWriteWidget
                {
                    Id = 31,
                    Name = "rolled-back",
                    Active = true,
                    Amount = 31.31m,
                }
            );

            Assert.Equal(
                1,
                await context
                    .SaveChangesAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
            await transaction
                .RollbackAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            context.ChangeTracker.Clear();

            int rowCount = await context
                .WriteWidgets.AsNoTracking()
                .CountAsync(widget => widget.Id == 31, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            if (rowCount != 0)
            {
                Assert.Skip(
                    "The pinned StarRocks integration image accepted ROLLBACK WORK but made the inserted EF row visible."
                );
            }

            Assert.Equal(0, rowCount);
        }
        finally
        {
            await DropWriteWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "EF1003:Method inserts concatenated strings directly into the SQL",
        Justification = "The migration cleanup and verification SQL is assembled only from fixed sanitized identifiers and parameter placeholders."
    )]
    public async Task MigrateAsync_CreatesValidStarRocksTable()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using DotRocksTestContext setupContext = CreateLiveContext();
        await EnsureLinqDatabaseAsync(setupContext).ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier(MigrationWidgetTableName),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier("__EFMigrationsHistory"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        var interceptor = new CapturingCommandInterceptor();
        using var context = new MigrationTestContext(
            CreateMigrationContextOptions(
                BuildDatabaseConnectionString(LinqDatabaseName),
                interceptor
            )
        );

        await context
            .Database.MigrateAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(
            await TableExistsAsync(context, LinqDatabaseName, MigrationWidgetTableName)
                .ConfigureAwait(true)
        );

        interceptor.Clear();
        Assert.Equal(
            1,
            await context
                .Database.ExecuteSqlRawAsync(
                    "INSERT INTO "
                        + DelimitIdentifier(MigrationWidgetTableName)
                        + " ("
                        + DelimitIdentifier("id")
                        + ", "
                        + DelimitIdentifier("name")
                        + ") VALUES ({0}, {1})",
                    [1, "created"],
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true)
        );
        CapturedCommand insertCommand = Assert.Single(
            interceptor.NonQueryCommands("INSERT"),
            command =>
                command.CommandText.Contains(MigrationWidgetTableName, StringComparison.Ordinal)
        );
        Assert.Contains("@p", insertCommand.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("created", insertCommand.CommandText, StringComparison.Ordinal);
        Assert.Contains("created", insertCommand.ParameterValues());

        EfMigrationWidget row = await context
            .MigrationWidgets.AsNoTracking()
            .SingleAsync(widget => widget.Id == 1, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("created", row.Name);

        Assert.Equal(1, await HistoryRowCountAsync(context, MigrationId).ConfigureAwait(true));

        interceptor.Clear();
        await context
            .Database.MigrateAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.DoesNotContain(
            interceptor.Commands,
            command =>
                command.CommandText.Contains(MigrationWidgetTableName, StringComparison.Ordinal)
                    && command.CommandText.StartsWith(
                        "CREATE TABLE",
                        StringComparison.OrdinalIgnoreCase
                    )
                || command.CommandText.Contains(MigrationId, StringComparison.Ordinal)
                    && command.CommandText.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(1, await HistoryRowCountAsync(context, MigrationId).ConfigureAwait(true));
        Assert.Equal(
            1,
            await context
                .MigrationWidgets.AsNoTracking()
                .CountAsync(widget => widget.Id == 1, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "EF1003:Method inserts concatenated strings directly into the SQL",
        Justification = "The migration cleanup, verification, and SHOW CREATE SQL is assembled only from fixed sanitized identifiers and parameter placeholders."
    )]
    public async Task MigrateAsync_CreatesConfiguredTableShape()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using DotRocksTestContext setupContext = CreateLiveContext();
        await EnsureLinqDatabaseAsync(setupContext).ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier(TableShapeMigrationWidgetTableName),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier("__EFMigrationsHistory"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        using var context = new TableShapeMigrationTestContext(
            CreateTableShapeMigrationContextOptions(BuildDatabaseConnectionString(LinqDatabaseName))
        );

        await context
            .Database.MigrateAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(
            1,
            await context
                .Database.ExecuteSqlRawAsync(
                    "INSERT INTO "
                        + DelimitIdentifier(TableShapeMigrationWidgetTableName)
                        + " ("
                        + DelimitIdentifier("id")
                        + ", "
                        + DelimitIdentifier("name")
                        + ") VALUES ({0}, {1})",
                    [1, "configured"],
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true)
        );

        EfMigrationWidget row = await context
            .MigrationWidgets.AsNoTracking()
            .SingleAsync(widget => widget.Id == 1, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        string createTableSql = await ReadShowCreateTableAsync(
                context,
                TableShapeMigrationWidgetTableName
            )
            .ConfigureAwait(true);

        Assert.Equal("configured", row.Name);
        Assert.Contains("PRIMARY KEY", createTableSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`id`", createTableSql, StringComparison.Ordinal);
        Assert.Contains("BUCKETS 3", createTableSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replication_num", createTableSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", createTableSql, StringComparison.Ordinal);
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "EF1003:Method inserts concatenated strings directly into the SQL",
        Justification = "The migration cleanup and verification SQL is assembled only from fixed sanitized identifiers and constants."
    )]
    public async Task MigrateAsync_DownMigrationDropsTableAndHistoryRow()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using DotRocksTestContext setupContext = CreateLiveContext();
        await EnsureLinqDatabaseAsync(setupContext).ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier(MigrationWidgetTableName),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier("__EFMigrationsHistory"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        using var context = new MigrationTestContext(
            CreateMigrationContextOptions(BuildDatabaseConnectionString(LinqDatabaseName))
        );

        await context
            .Database.MigrateAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(
            await TableExistsAsync(context, LinqDatabaseName, MigrationWidgetTableName)
                .ConfigureAwait(true)
        );
        Assert.Equal(1, await HistoryRowCountAsync(context, MigrationId).ConfigureAwait(true));

        await context
            .GetService<IMigrator>()
            .MigrateAsync("0", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.False(
            await TableExistsAsync(context, LinqDatabaseName, MigrationWidgetTableName)
                .ConfigureAwait(true)
        );
        Assert.Equal(0, await HistoryRowCountAsync(context, MigrationId).ConfigureAwait(true));
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "EF1003:Method inserts concatenated strings directly into the SQL",
        Justification = "The migration cleanup, verification, and write SQL is assembled only from fixed sanitized identifiers and parameter placeholders."
    )]
    public async Task MigrateAsync_EnsureSchemaCreatesDatabaseTableHistoryAndQueryableRows()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using DotRocksTestContext setupContext = CreateLiveContext();
        await EnsureLinqDatabaseAsync(setupContext).ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP DATABASE IF EXISTS " + DelimitIdentifier(EnsureSchemaMigrationDatabaseName),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier("__EFMigrationsHistory"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        try
        {
            using var context = new EnsureSchemaMigrationTestContext(
                CreateDbContextOptions<EnsureSchemaMigrationTestContext>(
                    BuildDatabaseConnectionString(LinqDatabaseName)
                )
            );

            await context
                .Database.MigrateAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.True(
                await DatabaseExistsAsync(context, EnsureSchemaMigrationDatabaseName)
                    .ConfigureAwait(true)
            );
            Assert.True(
                await TableExistsAsync(
                        context,
                        EnsureSchemaMigrationDatabaseName,
                        EnsureSchemaMigrationWidgetTableName
                    )
                    .ConfigureAwait(true)
            );
            Assert.True(
                await TableExistsAsync(context, LinqDatabaseName, "__EFMigrationsHistory")
                    .ConfigureAwait(true)
            );
            Assert.Equal(
                1,
                await HistoryRowCountAsync(context, EnsureSchemaMigrationId).ConfigureAwait(true)
            );

            Assert.Equal(
                1,
                await context
                    .Database.ExecuteSqlRawAsync(
                        "INSERT INTO "
                            + DelimitIdentifier(EnsureSchemaMigrationDatabaseName)
                            + "."
                            + DelimitIdentifier(EnsureSchemaMigrationWidgetTableName)
                            + " ("
                            + DelimitIdentifier("id")
                            + ", "
                            + DelimitIdentifier("name")
                            + ") VALUES ({0}, {1})",
                        [1, "ensure-schema"],
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            );

            EfMigrationWidget row = await context
                .MigrationWidgets.AsNoTracking()
                .SingleAsync(widget => widget.Id == 1, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal("ensure-schema", row.Name);
        }
        finally
        {
            await setupContext
                .Database.ExecuteSqlRawAsync(
                    "DROP DATABASE IF EXISTS "
                        + DelimitIdentifier(EnsureSchemaMigrationDatabaseName),
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        }
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "EF1003:Method inserts concatenated strings directly into the SQL",
        Justification = "The migration cleanup and verification SQL is assembled only from fixed sanitized identifiers and constants."
    )]
    public async Task MigrateAsync_UnsupportedSchemaMutationFailsBeforePartialMigrationSql()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using DotRocksTestContext setupContext = CreateLiveContext();
        await EnsureLinqDatabaseAsync(setupContext).ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier(UnsupportedMigrationWidgetTableName),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier("__EFMigrationsHistory"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        var interceptor = new CapturingCommandInterceptor();
        using var context = new UnsupportedMigrationTestContext(
            CreateDbContextOptions<UnsupportedMigrationTestContext>(
                BuildDatabaseConnectionString(LinqDatabaseName),
                interceptor
            )
        );

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            context
                .GetService<IMigrator>()
                .MigrateAsync(cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Contains("ADD COLUMN", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            interceptor.Commands,
            command =>
                command.CommandText.Contains(
                    UnsupportedMigrationWidgetTableName,
                    StringComparison.Ordinal
                )
                && command.CommandText.StartsWith(
                    "CREATE TABLE",
                    StringComparison.OrdinalIgnoreCase
                )
        );
        Assert.False(
            await TableExistsAsync(context, LinqDatabaseName, UnsupportedMigrationWidgetTableName)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            0,
            await HistoryRowCountAsync(context, UnsupportedMigrationId).ConfigureAwait(true)
        );
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "EF1003:Method inserts concatenated strings directly into the SQL",
        Justification = "The migration cleanup and verification SQL is assembled only from fixed sanitized identifiers and constants."
    )]
    public async Task MigrateAsync_DownMigrationSupportsDropTableOnlyAndRejectsDropDatabase()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using DotRocksTestContext setupContext = CreateLiveContext();
        await EnsureLinqDatabaseAsync(setupContext).ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier(UnsupportedDownMigrationWidgetTableName),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier("__EFMigrationsHistory"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        var interceptor = new CapturingCommandInterceptor();
        using var context = new UnsupportedDownMigrationTestContext(
            CreateDbContextOptions<UnsupportedDownMigrationTestContext>(
                BuildDatabaseConnectionString(LinqDatabaseName),
                interceptor
            )
        );

        await context
            .Database.MigrateAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(
            await TableExistsAsync(
                    context,
                    LinqDatabaseName,
                    UnsupportedDownMigrationWidgetTableName
                )
                .ConfigureAwait(true)
        );

        interceptor.Clear();
        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            context.GetService<IMigrator>().MigrateAsync("0", TestContext.Current.CancellationToken)
        );

        Assert.Contains("DROP DATABASE", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            interceptor.Commands,
            command =>
                command.CommandText.Contains(
                    UnsupportedDownMigrationWidgetTableName,
                    StringComparison.Ordinal
                )
                && command.CommandText.StartsWith("DROP TABLE", StringComparison.OrdinalIgnoreCase)
        );
        Assert.True(
            await TableExistsAsync(
                    context,
                    LinqDatabaseName,
                    UnsupportedDownMigrationWidgetTableName
                )
                .ConfigureAwait(true)
        );
        Assert.Equal(
            1,
            await HistoryRowCountAsync(context, UnsupportedDownMigrationId).ConfigureAwait(true)
        );
        await setupContext
            .Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS "
                    + DelimitIdentifier(LinqDatabaseName)
                    + "."
                    + DelimitIdentifier(UnsupportedDownMigrationWidgetTableName),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task UnsupportedLinqTranslation_ThrowsInvalidOperationException()
    {
        await using var context = CreateContext("Server=127.0.0.1;Port=9030;User ID=root");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context
                .Widgets.Where(widget => IsInteresting(widget.Name))
                .ToListAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task FromSqlRaw_MaterializesEntity()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();

        DotRocksRow row = await context
            .Rows.FromSqlRaw("SELECT 7 AS id, 'seven' AS name")
            .SingleAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(7, row.Id);
        Assert.Equal("seven", row.Name);
    }

    [Fact]
    public async Task LinqWhereSelectFirstOrDefault_Translates()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            string? value = await context
                .Widgets.Where(widget => widget.Id == 2)
                .Select(widget => widget.Name)
                .FirstOrDefaultAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal("two", value);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LinqWhereComparisons_Translate()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            List<int> ids = await context
                .Widgets.Where(widget =>
                    widget.Id != 1
                    && widget.Id > 1
                    && widget.Id >= 2
                    && widget.Id < 4
                    && widget.Id <= 3
                )
                .OrderBy(widget => widget.Id)
                .Select(widget => widget.Id)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal([2, 3], ids);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The seed INSERT is built only from fixed sanitized identifiers and constants."
    )]
    public async Task LinqContains_EscapesLikeWildcardsLiterally()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            string insertSql =
                "INSERT INTO "
                + DelimitedWidgetTable()
                + " VALUES "
                + "(10, 1, 1, 1, 'a_b', TRUE, '2026-06-19 10:00:00', 1.00, NULL), "
                + "(11, 1, 1, 1, 'aXb', TRUE, '2026-06-19 10:00:00', 1.00, NULL), "
                + "(12, 1, 1, 1, 'a%b', TRUE, '2026-06-19 10:00:00', 1.00, NULL), "
                + "(13, 1, 1, 1, 'aYb', TRUE, '2026-06-19 10:00:00', 1.00, NULL)";
            await context
                .Database.ExecuteSqlRawAsync(insertSql, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            // '_' must match a literal underscore, not any single character.
            List<string> underscore = await context
                .Widgets.Where(widget => widget.Name.Contains("a_b"))
                .OrderBy(widget => widget.Id)
                .Select(widget => widget.Name)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(["a_b"], underscore);

            // '%' must match a literal percent, not any sequence.
            List<string> percent = await context
                .Widgets.Where(widget => widget.Name.Contains("a%b"))
                .OrderBy(widget => widget.Id)
                .Select(widget => widget.Name)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(["a%b"], percent);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LinqAndAlsoOrElseAndNullableComparisons_Translate()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            List<int> ids = await context
                .Widgets.Where(widget =>
                    (widget.Category == 2 || widget.OptionalScore == null)
                    && (widget.OptionalScore == null || widget.OptionalScore >= 20)
                )
                .OrderBy(widget => widget.Id)
                .Select(widget => widget.Id)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal([1, 3], ids);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LinqOrderByThenByTakeSkip_Translates()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            List<string> names = await context
                .Widgets.OrderBy(widget => widget.Category)
                .ThenBy(widget => widget.Priority)
                .Select(widget => widget.Name)
                .Skip(1)
                .Take(2)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(["one", "three"], names);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LinqCountAndAny_Translate()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            int activeCount = await context
                .Widgets.CountAsync(widget => widget.Active, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            bool hasLargeAmount = await context
                .Widgets.AnyAsync(
                    widget => widget.Amount > 30m,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            bool hasMissingName = await context
                .Widgets.AnyAsync(
                    widget => widget.Name == "missing",
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            Assert.Equal(2, activeCount);
            Assert.True(hasLargeAmount);
            Assert.False(hasMissingName);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LinqProjectionAndToList_Translate()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            WidgetProjection projection = await context
                .Widgets.Where(widget => widget.Id == 2)
                .Select(widget => new WidgetProjection(widget.Id, widget.Name, widget.Amount))
                .SingleAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var anonymousRows = await context
                .Widgets.Where(widget => widget.Id <= 2)
                .OrderBy(widget => widget.Id)
                .Select(widget => new
                {
                    widget.Id,
                    widget.Name,
                    widget.Active,
                })
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(new WidgetProjection(2, "two", 23.45m), projection);
            Assert.Equal(2, anonymousRows.Count);
            Assert.Equal("one", anonymousRows[0].Name);
            Assert.True(anonymousRows[0].Active);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public void LinqCapturedParameters_RenderAtPlaceholdersInGeneratedSql()
    {
        using var context = CreateContext("Server=127.0.0.1;Port=9030;User ID=root");
        int id = 2;
        string name = "two";
        decimal amount = 23.45m;
        int? optionalScore = 20;
        bool active = false;

        string sql = context
            .Widgets.Where(widget =>
                widget.Id == id
                && widget.Name == name
                && widget.Amount == amount
                && widget.OptionalScore == optionalScore
                && widget.Active == active
            )
            .Select(widget => widget.Id)
            .ToQueryString();
        string sqlBody = StripParameterPreamble(sql);

        Assert.Contains("@id", sqlBody, StringComparison.Ordinal);
        Assert.Contains("@name", sqlBody, StringComparison.Ordinal);
        Assert.Contains("@amount", sqlBody, StringComparison.Ordinal);
        Assert.Contains("@optionalScore", sqlBody, StringComparison.Ordinal);
        Assert.Contains("@active", sqlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("'two'", sqlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("23.45", sqlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FALSE", sqlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinqEfParameter_ForcesAtPlaceholdersInGeneratedSql()
    {
        using var context = CreateContext("Server=127.0.0.1;Port=9030;User ID=root");
        int id = 2;
        string name = "two";
        decimal amount = 23.45m;
        int? optionalScore = 20;
        bool active = false;

        string sql = context
            .Widgets.Where(widget =>
                widget.Id == EF.Parameter(id)
                && widget.Name == EF.Parameter(name)
                && widget.Amount == EF.Parameter(amount)
                && widget.OptionalScore == EF.Parameter(optionalScore)
                && widget.Active == EF.Parameter(active)
            )
            .Select(widget => widget.Id)
            .ToQueryString();
        string sqlBody = StripParameterPreamble(sql);

        Assert.Contains("@id", sqlBody, StringComparison.Ordinal);
        Assert.Contains("@name", sqlBody, StringComparison.Ordinal);
        Assert.Contains("@amount", sqlBody, StringComparison.Ordinal);
        Assert.Contains("@optionalScore", sqlBody, StringComparison.Ordinal);
        Assert.Contains("@active", sqlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("'two'", sqlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("23.45", sqlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FALSE", sqlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LinqCapturedParameters_UseAtPlaceholdersAndReturnExpectedRows()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        var interceptor = new CapturingCommandInterceptor();
        await using var context = CreateLiveContext(interceptor);
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            int id = 2;
            string name = "two";
            decimal amount = 23.45m;
            int? optionalScore = 20;
            bool active = false;
            IQueryable<int> query = context
                .Widgets.Where(widget =>
                    widget.Id == id
                    && widget.Name == name
                    && widget.Amount == amount
                    && widget.OptionalScore == optionalScore
                    && widget.Active == active
                )
                .Select(widget => widget.Id);

            string sql = query.ToQueryString();
            string sqlBody = StripParameterPreamble(sql);
            Assert.Contains("@id", sqlBody, StringComparison.Ordinal);
            Assert.Contains("@name", sqlBody, StringComparison.Ordinal);
            Assert.Contains("@amount", sqlBody, StringComparison.Ordinal);
            Assert.Contains("@optionalScore", sqlBody, StringComparison.Ordinal);
            Assert.Contains("@active", sqlBody, StringComparison.Ordinal);
            interceptor.Clear();

            int value = await query
                .SingleAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            CapturedCommand command = interceptor.SingleSelectCommand(WidgetTableName);

            Assert.Equal(2, value);
            Assert.Contains("@id", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("@name", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("@amount", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("@optionalScore", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("@active", command.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("'two'", command.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("23.45", command.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("FALSE", command.CommandText, StringComparison.OrdinalIgnoreCase);
            AssertParameter(command, "@id", 2, DbType.Int32);
            AssertParameter(command, "@name", name, DbType.String);
            AssertParameter(command, "@amount", amount, DbType.Decimal);
            AssertParameter(command, "@optionalScore", optionalScore, DbType.Int32);
            AssertParameter(command, "@active", active, DbType.Boolean);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LinqEfParameter_UsesAtPlaceholdersAndReturnExpectedRows()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        var interceptor = new CapturingCommandInterceptor();
        await using var context = CreateLiveContext(interceptor);
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            int id = 2;
            string name = "two";
            decimal amount = 23.45m;
            int? optionalScore = 20;
            bool active = false;
            IQueryable<int> query = context
                .Widgets.Where(widget =>
                    widget.Id == EF.Parameter(id)
                    && widget.Name == EF.Parameter(name)
                    && widget.Amount == EF.Parameter(amount)
                    && widget.OptionalScore == EF.Parameter(optionalScore)
                    && widget.Active == EF.Parameter(active)
                )
                .Select(widget => widget.Id);

            interceptor.Clear();
            int value = await query
                .SingleAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            CapturedCommand command = interceptor.SingleSelectCommand(WidgetTableName);

            Assert.Equal(2, value);
            Assert.Contains("@id", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("@name", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("@amount", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("@optionalScore", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("@active", command.CommandText, StringComparison.Ordinal);
            AssertParameter(command, "@id", 2, DbType.Int32);
            AssertParameter(command, "@name", name, DbType.String);
            AssertParameter(command, "@amount", amount, DbType.Decimal);
            AssertParameter(command, "@optionalScore", optionalScore, DbType.Int32);
            AssertParameter(command, "@active", active, DbType.Boolean);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "EF1003:Method inserts concatenated strings directly into the SQL",
        Justification = "The insert command is assembled only from fixed sanitized identifiers and parameter placeholders."
    )]
    public async Task LinqStringAndNullParameters_KeepSensitiveValuesOutOfSqlText()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        var interceptor = new CapturingCommandInterceptor();
        await using var context = CreateLiveContext(interceptor);
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        const string quotedName = "quote'\\name";
        const string jsonName = """{"kind":"alpha","text":"quote'\slash\\"}""";
        try
        {
            await context
                .Database.ExecuteSqlRawAsync(
                    "INSERT INTO "
                        + DelimitedWidgetTable()
                        + " (id, category, priority, big_value, name, active, created_at, amount, optional_score) "
                        + "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, NULL), "
                        + "({8}, {9}, {10}, {11}, {12}, {13}, {14}, {15}, NULL)",
                    [
                        4,
                        3,
                        1,
                        10000000004L,
                        quotedName,
                        true,
                        new DateTime(2026, 6, 19, 13, 0, 0),
                        45.67m,
                        5,
                        3,
                        1,
                        10000000005L,
                        jsonName,
                        false,
                        new DateTime(2026, 6, 19, 14, 0, 0),
                        56.78m,
                    ],
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            string name = quotedName;
            int? optionalScore = null;
            interceptor.Clear();
            int quotedId = await context
                .Widgets.Where(widget => widget.Name == name && EF.Parameter(optionalScore) == null)
                .Select(widget => widget.Id)
                .SingleAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            CapturedCommand quotedCommand = interceptor.SingleSelectCommand(WidgetTableName);

            Assert.Equal(4, quotedId);
            Assert.Contains("@name", quotedCommand.CommandText, StringComparison.Ordinal);
            Assert.Contains("@optionalScore", quotedCommand.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain(quotedName, quotedCommand.CommandText, StringComparison.Ordinal);
            AssertParameter(quotedCommand, "@name", quotedName, DbType.String);
            AssertNullParameter(quotedCommand, "@optionalScore");

            name = jsonName;
            interceptor.Clear();
            int jsonId = await context
                .Widgets.Where(widget => widget.Name == name && EF.Parameter(optionalScore) == null)
                .Select(widget => widget.Id)
                .SingleAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            CapturedCommand jsonCommand = interceptor.SingleSelectCommand(WidgetTableName);

            Assert.Equal(5, jsonId);
            Assert.Contains("@name", jsonCommand.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain(jsonName, jsonCommand.CommandText, StringComparison.Ordinal);
            AssertParameter(jsonCommand, "@name", jsonName, DbType.String);
            AssertNullParameter(jsonCommand, "@optionalScore");
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "EF1003:Method inserts concatenated strings directly into the SQL",
        Justification = "The insert command is assembled only from fixed sanitized identifiers and parameter placeholders."
    )]
    public async Task LinqDotRocksDecimalParameter_UsesAtPlaceholderAndReturnsExpectedRows()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        var interceptor = new CapturingCommandInterceptor();
        await using var context = CreateLiveContext(interceptor);
        await EnsureHighPrecisionTableAsync(context).ConfigureAwait(true);

        try
        {
            DotRocksDecimal value = DotRocksDecimal.Parse(
                "1234567890123456789012345678901234.9000"
            );
            await context
                .Database.ExecuteSqlRawAsync(
                    "INSERT INTO "
                        + DelimitedHighPrecisionTable()
                        + " (id, value) VALUES ({0}, {1})",
                    [1, value],
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            DotRocksDecimal target = value;
            IQueryable<int> query = context
                .HighPrecisionRows.Where(row => row.Value == target)
                .Select(row => row.Id);
            string sqlBody = StripParameterPreamble(query.ToQueryString());
            Assert.Contains("@target", sqlBody, StringComparison.Ordinal);
            Assert.DoesNotContain(value.ToString(), sqlBody, StringComparison.Ordinal);

            interceptor.Clear();
            int id = await query
                .SingleAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            CapturedCommand command = interceptor.SingleSelectCommand("high_precision_values");

            Assert.Equal(1, id);
            Assert.Contains("@target", command.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain(value.ToString(), command.CommandText, StringComparison.Ordinal);
            AssertParameter(command, "@target", value, DbType.Decimal);
        }
        finally
        {
            await DropHighPrecisionTableAsync(context).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LinqContainsStringPredicatesDescendingDistinctAndAggregates_Translate()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            int[] ids = [1, 3];
            List<string> inNames = await context
                .Widgets.Where(widget => ids.Contains(widget.Id))
                .OrderByDescending(widget => widget.Id)
                .Select(widget => widget.Name)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            List<string> stringMatches = await context
                .Widgets.Where(widget =>
                    widget.Name.StartsWith("tw")
                    || widget.Name.EndsWith("ne")
                    || widget.Name.Contains("wo")
                    || widget.Name.Contains("hr")
                )
                .OrderBy(widget => widget.Id)
                .Select(widget => widget.Name)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            List<int> categories = await context
                .Widgets.Select(widget => widget.Category)
                .Distinct()
                .OrderBy(category => category)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            int min = await context
                .Widgets.MinAsync(widget => widget.Id, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            int max = await context
                .Widgets.MaxAsync(widget => widget.Id, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            int sum = await context
                .Widgets.SumAsync(widget => widget.Id, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            double average = await context
                .Widgets.AverageAsync(widget => widget.Id, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(["three", "one"], inNames);
            Assert.Equal(["one", "two", "three"], stringMatches);
            Assert.Equal([1, 2], categories);
            Assert.Equal(1, min);
            Assert.Equal(3, max);
            Assert.Equal(6, sum);
            Assert.Equal(2.0d, average);
        }
        finally
        {
            await DropWidgetTableAsync(context).ConfigureAwait(true);
        }
    }

    private static DotRocksTestContext CreateLiveContext(params IInterceptor[] interceptors) =>
        CreateContext(IntegrationTestEnvironment.ConnectionString, interceptors);

    private static DotRocksTestContext CreateContext(
        string connectionString,
        params IInterceptor[] interceptors
    )
    {
        var optionsBuilder = new DbContextOptionsBuilder<DotRocksTestContext>();
        optionsBuilder.UseStarRocks(connectionString);
        if (interceptors.Length > 0)
        {
            optionsBuilder.AddInterceptors(interceptors);
        }

        return new DotRocksTestContext(optionsBuilder.Options);
    }

    private static DbContextOptions<MigrationTestContext> CreateMigrationContextOptions(
        string connectionString,
        params IInterceptor[] interceptors
    ) => CreateDbContextOptions<MigrationTestContext>(connectionString, interceptors);

    private static DbContextOptions<TableShapeMigrationTestContext> CreateTableShapeMigrationContextOptions(
        string connectionString,
        params IInterceptor[] interceptors
    ) => CreateDbContextOptions<TableShapeMigrationTestContext>(connectionString, interceptors);

    private static DbContextOptions<TContext> CreateDbContextOptions<TContext>(
        string connectionString,
        params IInterceptor[] interceptors
    )
        where TContext : DbContext
    {
        var optionsBuilder = new DbContextOptionsBuilder<TContext>();
        optionsBuilder.UseStarRocks(connectionString);
        if (interceptors.Length > 0)
        {
            optionsBuilder.AddInterceptors(interceptors);
        }

        return optionsBuilder.Options;
    }

    private static async Task EnsureLinqDatabaseAsync(DotRocksTestContext context)
    {
        string sql = "CREATE DATABASE IF NOT EXISTS " + DelimitIdentifier(LinqDatabaseName);
        await context
            .Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static async Task EnsureWidgetTableAsync(DotRocksTestContext context)
    {
        await EnsureLinqDatabaseAsync(context).ConfigureAwait(true);
        await DropWidgetTableAsync(context).ConfigureAwait(true);
        string createSql = $"""
            CREATE TABLE {DelimitedWidgetTable()} (
                id INT NOT NULL,
                category INT NOT NULL,
                priority INT NOT NULL,
                big_value BIGINT NOT NULL,
                name VARCHAR(64) NOT NULL,
                active BOOLEAN NOT NULL,
                created_at DATETIME NOT NULL,
                amount DECIMAL(10, 2) NOT NULL,
                optional_score INT NULL
            )
            DUPLICATE KEY(id)
            DISTRIBUTED BY HASH(id) BUCKETS 1
            PROPERTIES ('replication_num' = '1')
            """;
        string insertSql =
            "INSERT INTO "
            + DelimitedWidgetTable()
            + " VALUES "
            + "(1, 1, 2, 10000000001, 'one', TRUE, '2026-06-19 10:00:00', 12.34, NULL), "
            + "(2, 1, 1, 10000000002, 'two', FALSE, '2026-06-19 11:00:00', 23.45, 20), "
            + "(3, 2, 1, 10000000003, 'three', TRUE, '2026-06-19 12:00:00', 34.56, 30)";

        await context
            .Database.ExecuteSqlRawAsync(createSql, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        await context
            .Database.ExecuteSqlRawAsync(insertSql, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static Task<int> DropWidgetTableAsync(DotRocksTestContext context)
    {
        string sql = "DROP TABLE IF EXISTS " + DelimitedWidgetTable();
        return context.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    private static async Task EnsureWriteWidgetTableAsync(DotRocksTestContext context)
    {
        await EnsureLinqDatabaseAsync(context).ConfigureAwait(true);
        await DropWriteWidgetTableAsync(context).ConfigureAwait(true);
        string createSql = $"""
            CREATE TABLE {DelimitedWriteWidgetTable()} (
                id INT NOT NULL,
                name VARCHAR(64) NOT NULL,
                active BOOLEAN NOT NULL,
                amount DECIMAL(10, 2) NOT NULL
            )
            PRIMARY KEY(id)
            DISTRIBUTED BY HASH(id) BUCKETS 1
            PROPERTIES ('replication_num' = '1')
            """;

        await context
            .Database.ExecuteSqlRawAsync(createSql, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static Task<int> DropWriteWidgetTableAsync(DotRocksTestContext context)
    {
        string sql = "DROP TABLE IF EXISTS " + DelimitedWriteWidgetTable();
        return context.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    private static async Task EnsureHighPrecisionTableAsync(DotRocksTestContext context)
    {
        await EnsureLinqDatabaseAsync(context).ConfigureAwait(true);
        await DropHighPrecisionTableAsync(context).ConfigureAwait(true);
        string createSql = $"""
            CREATE TABLE {DelimitedHighPrecisionTable()} (
                id INT NOT NULL,
                value DECIMAL(38, 4) NOT NULL
            )
            DUPLICATE KEY(id)
            DISTRIBUTED BY HASH(id) BUCKETS 1
            PROPERTIES ('replication_num' = '1')
            """;

        await context
            .Database.ExecuteSqlRawAsync(createSql, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static Task<int> DropHighPrecisionTableAsync(DotRocksTestContext context)
    {
        string sql = "DROP TABLE IF EXISTS " + DelimitedHighPrecisionTable();
        return context.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The test command is assembled only from fixed sanitized identifiers and constants."
    )]
    private static string CreateParameterizedVerificationSql() =>
        "SELECT COUNT(*) FROM "
        + DelimitedWidgetTable()
        + " WHERE id = 42 AND name = 'parameterized'";

    private static string DelimitedWidgetTable() =>
        DelimitIdentifier(LinqDatabaseName) + "." + DelimitIdentifier(WidgetTableName);

    private static string DelimitedWriteWidgetTable() =>
        DelimitIdentifier(LinqDatabaseName) + "." + DelimitIdentifier(WriteWidgetTableName);

    private static string DelimitedHighPrecisionTable() =>
        DelimitIdentifier(LinqDatabaseName) + "." + DelimitIdentifier("high_precision_values");

    private static string CreateUniqueDatabaseName() =>
        "dotrocks_ef_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];

    private static string BuildDatabaseConnectionString(string database)
    {
        var builder = new DotRocksConnectionStringBuilder(
            IntegrationTestEnvironment.ConnectionString
        )
        {
            Database = database,
        };
        return builder.ConnectionString;
    }

    private static string DelimitIdentifier(string identifier) =>
        "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The SHOW CREATE command is assembled only from a fixed sanitized table identifier."
    )]
    private static async Task<string> ReadShowCreateTableAsync(DbContext context, string tableName)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SHOW CREATE TABLE " + DelimitIdentifier(tableName);
        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        return reader.GetString(1);
    }

    private static async Task<bool> DatabaseExistsAsync(DbContext context, string databaseName)
    {
        int count = await context
            .Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM information_schema.schemata WHERE schema_name = {0}",
                databaseName
            )
            .SingleAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return count != 0;
    }

    private static async Task<bool> TableExistsAsync(
        DbContext context,
        string databaseName,
        string tableName
    )
    {
        int count = await context
            .Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM information_schema.tables WHERE table_schema = {0} AND table_name = {1}",
                databaseName,
                tableName
            )
            .SingleAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return count != 0;
    }

    [SuppressMessage(
        "Security",
        "EF1003:Method inserts concatenated strings directly into the SQL",
        Justification = "The migration history query is assembled only from fixed sanitized identifiers and parameter placeholders."
    )]
    private static Task<int> HistoryRowCountAsync(DbContext context, string migrationId) =>
        context
            .Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM "
                    + DelimitIdentifier("__EFMigrationsHistory")
                    + " WHERE "
                    + DelimitIdentifier("MigrationId")
                    + " = {0}",
                migrationId
            )
            .SingleAsync(TestContext.Current.CancellationToken);

    private static string StripParameterPreamble(string sql)
    {
        string[] lines = sql.Split('\n', StringSplitOptions.TrimEntries);
        return string.Join(
            Environment.NewLine,
            lines.Where(line => !line.StartsWith("--", StringComparison.Ordinal))
        );
    }

    private static void AssertParameter(
        CapturedCommand command,
        string name,
        object? value,
        DbType dbType
    )
    {
        CapturedParameter parameter = command.Parameter(name);
        Assert.Equal(dbType, parameter.DbType);
        Assert.Equal(value, parameter.Value);
    }

    private static void AssertNullParameter(CapturedCommand command, string name)
    {
        CapturedParameter parameter = command.Parameter(name);
        Assert.True(parameter.Value is null or DBNull);
    }

    private static void AssertParameterizedDml(CapturedCommand command)
    {
        Assert.Contains("@p", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("first", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("second", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("third", command.CommandText, StringComparison.Ordinal);
        Assert.NotEmpty(command.Parameters);
    }

    private static bool IsInteresting(string value) =>
        string.Equals(value, "two", StringComparison.Ordinal);

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class DotRocksTestContext(DbContextOptions<DotRocksTestContext> options)
        : DbContext(options)
    {
        public DbSet<DotRocksRow> Rows => Set<DotRocksRow>();

        public DbSet<DotRocksWidget> Widgets => Set<DotRocksWidget>();

        public DbSet<DotRocksDecimalRow> DecimalRows => Set<DotRocksDecimalRow>();

        public DbSet<HighPrecisionRow> HighPrecisionRows => Set<HighPrecisionRow>();

        public DbSet<DecimalOverflowRow> DecimalOverflowRows => Set<DecimalOverflowRow>();

        public DbSet<ExtraTypeRow> ExtraTypeRows => Set<ExtraTypeRow>();

        public DbSet<LargeIntRow> LargeIntRows => Set<LargeIntRow>();

        public DbSet<EfWriteWidget> WriteWidgets => Set<EfWriteWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DotRocksRow>().HasNoKey();
            modelBuilder.Entity<DotRocksDecimalRow>().HasNoKey();
            modelBuilder
                .Entity<DotRocksDecimalRow>()
                .Property(row => row.Value)
                .HasColumnName("value")
                .HasColumnType("decimal(38, 4)");
            modelBuilder
                .Entity<HighPrecisionRow>()
                .ToTable("high_precision_values", LinqDatabaseName)
                .HasKey(row => row.Id);
            modelBuilder.Entity<HighPrecisionRow>().Property(row => row.Id).ValueGeneratedNever();
            modelBuilder
                .Entity<HighPrecisionRow>()
                .Property(row => row.Value)
                .HasColumnType("decimal(38, 4)");
            modelBuilder.Entity<DecimalOverflowRow>().HasNoKey();
            modelBuilder
                .Entity<DecimalOverflowRow>()
                .Property(row => row.Value)
                .HasColumnName("value");
            modelBuilder.Entity<ExtraTypeRow>().HasNoKey();
            modelBuilder
                .Entity<ExtraTypeRow>()
                .Property(row => row.DateValue)
                .HasColumnName("date_value")
                .HasColumnType("date");
            modelBuilder
                .Entity<ExtraTypeRow>()
                .Property(row => row.TimeValue)
                .HasColumnName("time_value")
                .HasColumnType("time");
            modelBuilder
                .Entity<ExtraTypeRow>()
                .Property(row => row.GuidValue)
                .HasColumnName("guid_value")
                .HasColumnType("char(36)");
            modelBuilder
                .Entity<ExtraTypeRow>()
                .Property(row => row.JsonValue)
                .HasColumnName("json_value")
                .HasColumnType("json");
            modelBuilder.Entity<LargeIntRow>().HasNoKey();
            modelBuilder
                .Entity<LargeIntRow>()
                .Property(row => row.Value)
                .HasColumnName("value")
                .HasColumnType("largeint");
            modelBuilder
                .Entity<DotRocksWidget>()
                .ToTable(WidgetTableName, LinqDatabaseName)
                .HasKey(widget => widget.Id);
            modelBuilder
                .Entity<DotRocksWidget>()
                .Property(widget => widget.Id)
                .ValueGeneratedNever();
            modelBuilder.Entity<DotRocksWidget>().Property(widget => widget.Category);
            modelBuilder.Entity<DotRocksWidget>().Property(widget => widget.Priority);
            modelBuilder
                .Entity<DotRocksWidget>()
                .Property(widget => widget.BigValue)
                .HasColumnName("big_value")
                .HasColumnType("bigint");
            modelBuilder
                .Entity<DotRocksWidget>()
                .Property(widget => widget.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("datetime");
            modelBuilder
                .Entity<DotRocksWidget>()
                .Property(widget => widget.Amount)
                .HasColumnType("decimal(10, 2)");
            modelBuilder
                .Entity<DotRocksWidget>()
                .Property(widget => widget.OptionalScore)
                .HasColumnName("optional_score");
            modelBuilder
                .Entity<EfWriteWidget>()
                .ToTable(WriteWidgetTableName, LinqDatabaseName)
                .HasKey(widget => widget.Id);
            modelBuilder
                .Entity<EfWriteWidget>()
                .Property(widget => widget.Id)
                .ValueGeneratedNever();
            modelBuilder
                .Entity<EfWriteWidget>()
                .Property(widget => widget.Amount)
                .HasColumnType("decimal(10, 2)");
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class MigrationTestContext(DbContextOptions<MigrationTestContext> options)
        : DbContext(options)
    {
        public DbSet<EfMigrationWidget> MigrationWidgets => Set<EfMigrationWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .Entity<EfMigrationWidget>()
                .ToTable(MigrationWidgetTableName, LinqDatabaseName)
                .HasKey(widget => widget.Id);
            modelBuilder
                .Entity<EfMigrationWidget>()
                .Property(widget => widget.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            modelBuilder
                .Entity<EfMigrationWidget>()
                .Property(widget => widget.Name)
                .HasColumnName("name")
                .HasMaxLength(64);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class TableShapeMigrationTestContext(
        DbContextOptions<TableShapeMigrationTestContext> options
    ) : DbContext(options)
    {
        public DbSet<EfMigrationWidget> MigrationWidgets => Set<EfMigrationWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EfMigrationWidget>(entity =>
            {
                entity.ToTable(TableShapeMigrationWidgetTableName, LinqDatabaseName);
                entity.HasKey(widget => widget.Id);
                entity.Property(widget => widget.Id).ValueGeneratedNever().HasColumnName("id");
                entity.Property(widget => widget.Name).HasColumnName("name").HasMaxLength(64);
                entity.HasStarRocksPrimaryKey("id");
                entity.HasStarRocksHashDistribution(3, "id");
                entity.HasStarRocksReplicationNum(1);
            });
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class EnsureSchemaMigrationTestContext(
        DbContextOptions<EnsureSchemaMigrationTestContext> options
    ) : DbContext(options)
    {
        public DbSet<EfMigrationWidget> MigrationWidgets => Set<EfMigrationWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .Entity<EfMigrationWidget>()
                .ToTable(EnsureSchemaMigrationWidgetTableName, EnsureSchemaMigrationDatabaseName)
                .HasKey(widget => widget.Id);
            modelBuilder
                .Entity<EfMigrationWidget>()
                .Property(widget => widget.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            modelBuilder
                .Entity<EfMigrationWidget>()
                .Property(widget => widget.Name)
                .HasColumnName("name")
                .HasMaxLength(64);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class UnsupportedMigrationTestContext(
        DbContextOptions<UnsupportedMigrationTestContext> options
    ) : DbContext(options)
    {
        public DbSet<EfMigrationWidget> MigrationWidgets => Set<EfMigrationWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .Entity<EfMigrationWidget>()
                .ToTable(UnsupportedMigrationWidgetTableName, LinqDatabaseName)
                .HasKey(widget => widget.Id);
            modelBuilder
                .Entity<EfMigrationWidget>()
                .Property(widget => widget.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            modelBuilder
                .Entity<EfMigrationWidget>()
                .Property(widget => widget.Name)
                .HasColumnName("name")
                .HasMaxLength(64);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class UnsupportedDownMigrationTestContext(
        DbContextOptions<UnsupportedDownMigrationTestContext> options
    ) : DbContext(options)
    {
        public DbSet<EfMigrationWidget> MigrationWidgets => Set<EfMigrationWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .Entity<EfMigrationWidget>()
                .ToTable(UnsupportedDownMigrationWidgetTableName, LinqDatabaseName)
                .HasKey(widget => widget.Id);
            modelBuilder
                .Entity<EfMigrationWidget>()
                .Property(widget => widget.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            modelBuilder
                .Entity<EfMigrationWidget>()
                .Property(widget => widget.Name)
                .HasColumnName("name")
                .HasMaxLength(64);
        }
    }

    [DbContext(typeof(MigrationTestContext))]
    [Migration(MigrationId)]
    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core discovers this migration through assembly metadata."
    )]
    private sealed class CreateEfMigrationWidget : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: MigrationWidgetTableName,
                schema: LinqDatabaseName,
                columns: table => new
                {
                    Id = table.Column<int>(name: "id", nullable: false),
                    Name = table.Column<string>(name: "name", type: "varchar(64)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ef_migration_widgets", row => row.Id);
                }
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: MigrationWidgetTableName, schema: LinqDatabaseName);
        }
    }

    [DbContext(typeof(TableShapeMigrationTestContext))]
    [Migration(TableShapeMigrationId)]
    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core discovers this migration through assembly metadata."
    )]
    private sealed class CreateEfTableShapeMigrationWidget : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder
                .CreateTable(
                    name: TableShapeMigrationWidgetTableName,
                    schema: LinqDatabaseName,
                    columns: table => new
                    {
                        Id = table.Column<int>(name: "id", nullable: false),
                        Name = table.Column<string>(
                            name: "name",
                            type: "varchar(64)",
                            nullable: false
                        ),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_ef_table_shape_migration_widgets", row => row.Id);
                    }
                )
                .Annotation("DotRocks:KeyModel", DotRocksTableKeyModel.PrimaryKey)
                .Annotation("DotRocks:KeyColumns", IdStoreColumn)
                .Annotation("DotRocks:DistributionColumns", IdStoreColumn)
                .Annotation("DotRocks:DistributionBuckets", 3)
                .Annotation("DotRocks:ReplicationNum", 1);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: TableShapeMigrationWidgetTableName,
                schema: LinqDatabaseName
            );
        }
    }

    [DbContext(typeof(EnsureSchemaMigrationTestContext))]
    [Migration(EnsureSchemaMigrationId)]
    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core discovers this migration through assembly metadata."
    )]
    private sealed class CreateEnsureSchemaMigrationWidget : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(EnsureSchemaMigrationDatabaseName);
            migrationBuilder.CreateTable(
                name: EnsureSchemaMigrationWidgetTableName,
                schema: EnsureSchemaMigrationDatabaseName,
                columns: table => new
                {
                    Id = table.Column<int>(name: "id", nullable: false),
                    Name = table.Column<string>(name: "name", type: "varchar(64)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ef_ensure_schema_widgets", row => row.Id);
                }
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: EnsureSchemaMigrationWidgetTableName,
                schema: EnsureSchemaMigrationDatabaseName
            );
        }
    }

    [DbContext(typeof(UnsupportedMigrationTestContext))]
    [Migration(UnsupportedMigrationId)]
    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core discovers this migration through assembly metadata."
    )]
    private sealed class UnsupportedSchemaMutationMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: UnsupportedMigrationWidgetTableName,
                schema: LinqDatabaseName,
                columns: table => new
                {
                    Id = table.Column<int>(name: "id", nullable: false),
                    Name = table.Column<string>(name: "name", type: "varchar(64)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ef_unsupported_migration_widgets", row => row.Id);
                }
            );
            migrationBuilder.AddColumn<int>(
                name: "unsupported_value",
                schema: LinqDatabaseName,
                table: UnsupportedMigrationWidgetTableName,
                nullable: false,
                defaultValue: 0
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: UnsupportedMigrationWidgetTableName,
                schema: LinqDatabaseName
            );
        }
    }

    [DbContext(typeof(UnsupportedDownMigrationTestContext))]
    [Migration(UnsupportedDownMigrationId)]
    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core discovers this migration through assembly metadata."
    )]
    private sealed class UnsupportedDownMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: UnsupportedDownMigrationWidgetTableName,
                schema: LinqDatabaseName,
                columns: table => new
                {
                    Id = table.Column<int>(name: "id", nullable: false),
                    Name = table.Column<string>(name: "name", type: "varchar(64)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ef_unsupported_down_widgets", row => row.Id);
                }
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: UnsupportedDownMigrationWidgetTableName,
                schema: LinqDatabaseName
            );
            migrationBuilder.DropSchema(LinqDatabaseName);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core materializes this entity through query projection."
    )]
    private sealed class DotRocksRow
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core materializes this entity through query projection."
    )]
    private sealed class DotRocksWidget
    {
        public int Id { get; set; }

        public int Category { get; set; }

        public int Priority { get; set; }

        public long BigValue { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool Active { get; set; }

        public DateTime CreatedAt { get; set; }

        public decimal Amount { get; set; }

        public int? OptionalScore { get; set; }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core materializes this entity through query projection."
    )]
    private sealed class EfWriteWidget
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool Active { get; set; }

        public decimal Amount { get; set; }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core materializes this entity through query projection."
    )]
    private sealed class EfMigrationWidget
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core materializes this entity through query projection."
    )]
    private sealed class DotRocksDecimalRow
    {
        public DotRocksDecimal Value { get; set; }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core materializes this entity through query projection."
    )]
    private sealed class HighPrecisionRow
    {
        public int Id { get; set; }

        public DotRocksDecimal Value { get; set; }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core materializes this entity through query projection."
    )]
    private sealed class DecimalOverflowRow
    {
        public decimal Value { get; set; }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core materializes this entity through query projection."
    )]
    private sealed class ExtraTypeRow
    {
        public DateOnly DateValue { get; set; }

        public TimeOnly TimeValue { get; set; }

        public Guid GuidValue { get; set; }

        public string JsonValue { get; set; } = string.Empty;
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core materializes this entity through query projection."
    )]
    private sealed class LargeIntRow
    {
        public Int128 Value { get; set; }
    }

    private sealed record WidgetProjection(int Id, string Name, decimal Amount);

    private sealed class CapturingCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<CapturedCommand> _commands = [];

        public IReadOnlyList<CapturedCommand> Commands => _commands;

        public void Clear() => _commands.Clear();

        public CapturedCommand SingleSelectCommand(string tableName) =>
            Assert.Single(
                _commands,
                command =>
                    command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                    && command.CommandText.Contains(tableName, StringComparison.Ordinal)
            );

        public CapturedCommand SingleNonQueryCommand(string verb) =>
            Assert.Single(
                _commands,
                command => command.CommandText.StartsWith(verb, StringComparison.OrdinalIgnoreCase)
            );

        public CapturedCommand[] NonQueryCommands(string verb) =>
            _commands
                .Where(command =>
                    command.CommandText.StartsWith(verb, StringComparison.OrdinalIgnoreCase)
                )
                .ToArray();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result
        )
        {
            Capture(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            Capture(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result
        )
        {
            Capture(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            Capture(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command)
        {
            _commands.Add(
                new CapturedCommand(
                    command.CommandText,
                    command
                        .Parameters.Cast<DbParameter>()
                        .Select(parameter => new CapturedParameter(
                            parameter.ParameterName,
                            parameter.Value,
                            parameter.DbType
                        ))
                        .ToArray()
                )
            );
        }
    }

    private sealed record CapturedCommand(
        string CommandText,
        IReadOnlyList<CapturedParameter> Parameters
    )
    {
        public CapturedParameter Parameter(string name) =>
            Parameters.Single(parameter =>
                string.Equals(parameter.Name, name, StringComparison.Ordinal)
            );

        public object?[] ParameterValues() =>
            Parameters.Select(parameter => parameter.Value).ToArray();
    }

    private sealed record CapturedParameter(string Name, object? Value, DbType DbType);
}
