using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotRocks.EntityFrameworkCore.IntegrationTests;

public sealed class DotRocksEfCoreIntegrationTests
{
    private const string LinqDatabaseName = "dotrocks_ef_core_test";
    private const string WidgetTableName = "widgets";

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
            return;
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

        Assert.Equal(1, value);
    }

    [Fact]
    public async Task ExecuteSqlRawAsync_CreatesDatabase()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
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
            return;
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
            return;
        }

        await using var context = CreateLiveContext();
        await EnsureWidgetTableAsync(context).ConfigureAwait(true);

        try
        {
            string insertSql = "INSERT INTO " + DelimitedWidgetTable() + " VALUES ({0}, {1})";
            await context
                .Database.ExecuteSqlRawAsync(
                    insertSql,
                    [42, "parameterized"],
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            DbConnection connection = context.Database.GetDbConnection();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = CreateParameterizedVerificationSql();
            if (connection.State != System.Data.ConnectionState.Open)
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
    public void EnsureCreated_ThrowsNotSupportedException()
    {
        using var context = CreateContext("Server=127.0.0.1;Port=9030;User ID=root");

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            context.Database.EnsureCreated()
        );
        Assert.Contains("schema creation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migrate_ThrowsNotSupportedException()
    {
        using var context = CreateContext("Server=127.0.0.1;Port=9030;User ID=root");

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            context.Database.Migrate()
        );
        Assert.Contains("migrations", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveChangesAsync_ThrowsNotSupportedException()
    {
        await using var context = CreateContext("Server=127.0.0.1;Port=9030;User ID=root");
        context.Widgets.Add(new DotRocksWidget { Id = 1, Name = "one" });

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            context.SaveChangesAsync(TestContext.Current.CancellationToken)
        );
        Assert.Contains("SaveChanges", exception.Message, StringComparison.OrdinalIgnoreCase);
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
            return;
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
            return;
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

    private static DotRocksTestContext CreateLiveContext() =>
        CreateContext(IntegrationTestEnvironment.ConnectionString);

    private static DotRocksTestContext CreateContext(string connectionString)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DotRocksTestContext>();
        optionsBuilder.UseStarRocks(connectionString);
        return new DotRocksTestContext(optionsBuilder.Options);
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
                name VARCHAR(64) NOT NULL
            )
            DUPLICATE KEY(id)
            DISTRIBUTED BY HASH(id) BUCKETS 1
            PROPERTIES ('replication_num' = '1')
            """;
        string insertSql =
            "INSERT INTO " + DelimitedWidgetTable() + " VALUES (1, 'one'), (2, 'two')";

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

    private static string CreateUniqueDatabaseName() =>
        "dotrocks_ef_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];

    private static string DelimitIdentifier(string identifier) =>
        "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DotRocksRow>().HasNoKey();
            modelBuilder
                .Entity<DotRocksWidget>()
                .ToTable(WidgetTableName, LinqDatabaseName)
                .HasKey(widget => widget.Id);
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

    private sealed class DotRocksWidget
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
