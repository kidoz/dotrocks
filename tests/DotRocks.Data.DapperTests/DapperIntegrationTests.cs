using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Dapper;
using DotRocks.Data;
using Xunit;

namespace DotRocks.Data.DapperTests;

public sealed class DapperIntegrationTests
{
    private const string TransactionDatabaseName = "dotrocks_dapper";

    [Fact]
    public async Task QuerySingleAsync_ReturnsScalar()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        int value = await connection
            .QuerySingleAsync<int>(
                new CommandDefinition(
                    "SELECT 1",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);

        Assert.Equal(1, value);
    }

    [Fact]
    public async Task QuerySingleAsync_BindsParameters()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        int value = await connection
            .QuerySingleAsync<int>(
                new CommandDefinition(
                    "SELECT @value",
                    new { value = 42 },
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);

        Assert.Equal(42, value);
    }

    [Fact]
    public async Task QuerySingleAsync_MapsPoco()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        DapperRow row = await connection
            .QuerySingleAsync<DapperRow>(
                new CommandDefinition(
                    "SELECT 7 AS Id, 'seven' AS Name",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);

        Assert.Equal(7, row.Id);
        Assert.Equal("seven", row.Name);
    }

    [Fact]
    public async Task Transaction_Commit_MakesInsertedRowsVisible()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        string tableName = await CreateTransactionTableAsync().ConfigureAwait(true);
        try
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await UseTransactionDatabaseAsync(connection).ConfigureAwait(true);

            using System.Data.Common.DbTransaction transaction = await connection
                .BeginTransactionAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            await InsertDapperRowAsync(connection, transaction, tableName, id: 1, value: 10)
                .ConfigureAwait(true);

            await transaction
                .CommitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            int value = await ReadDapperValueAsync(tableName, id: 1).ConfigureAwait(true);

            Assert.Equal(10, value);
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task PooledConnections_OpenCloseSmoke_ReusesPhysicalConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        DotRocksConnection.ClearAllPools();
        string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
        long firstConnectionId;

        using (var first = new DotRocksConnection(connectionString))
        {
            await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);
            await first.CloseAsync().ConfigureAwait(true);
        }

        using (var second = new DotRocksConnection(connectionString))
        {
            await second.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            long secondConnectionId = await ReadConnectionIdAsync(second).ConfigureAwait(true);

            Assert.Equal(firstConnectionId, secondConnectionId);
            await second.CloseAsync().ConfigureAwait(true);
        }

        DotRocksConnection.ClearAllPools();
    }

    private static string BuildPoolingConnectionString(int maximumPoolSize)
    {
        var builder = new DotRocksConnectionStringBuilder(
            IntegrationTestEnvironment.ConnectionString
        )
        {
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = maximumPoolSize,
            ConnectionIdleTimeout = 300,
        };

        return builder.ConnectionString;
    }

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

    private static async Task<long> ReadConnectionIdAsync(DotRocksConnection connection) =>
        await connection
            .QuerySingleAsync<long>(
                new CommandDefinition(
                    "SELECT CONNECTION_ID()",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<string> CreateTransactionTableAsync()
    {
        string tableName =
            "dotrocks_dapper_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await connection
            .ExecuteAsync(
                new CommandDefinition(
                    $"CREATE DATABASE IF NOT EXISTS {TransactionDatabaseName}",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);

        using var databaseConnection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await databaseConnection
            .OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await databaseConnection
            .ExecuteAsync(
                new CommandDefinition(
                    $"""
                    CREATE TABLE {tableName}
                    (
                        id INT NOT NULL,
                        value INT NOT NULL
                    )
                    PRIMARY KEY(id)
                    DISTRIBUTED BY HASH(id) BUCKETS 1
                    PROPERTIES ("replication_num" = "1")
                    """,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        return tableName;
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task DropTableAsync(string tableName)
    {
        using var connection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await connection
            .ExecuteAsync(
                new CommandDefinition(
                    $"DROP TABLE IF EXISTS {tableName}",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test database names are constants controlled by the test."
    )]
    private static async Task UseTransactionDatabaseAsync(DotRocksConnection connection) =>
        await connection
            .ExecuteAsync(
                new CommandDefinition(
                    $"USE {TransactionDatabaseName}",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test SQL is built from internally generated table names and constant parameter placeholders."
    )]
    private static async Task InsertDapperRowAsync(
        DotRocksConnection connection,
        System.Data.Common.DbTransaction transaction,
        string tableName,
        int id,
        int value
    ) =>
        await connection
            .ExecuteAsync(
                new CommandDefinition(
                    $"INSERT INTO {tableName} SELECT @id, @value",
                    new { id, value },
                    transaction,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<int> ReadDapperValueAsync(string tableName, int id)
    {
        using var connection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        return await connection
            .QuerySingleAsync<int>(
                new CommandDefinition(
                    $"SELECT value FROM {tableName} WHERE id = @id",
                    new { id },
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "Dapper instantiates this test POCO through reflection."
    )]
    private sealed class DapperRow
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
