using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotRocks.Data.Loading;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

[Collection("StarRocks integration")]
public sealed class StreamLoadIntegrationTests
{
    private const string StreamLoadDatabaseName = "dotrocks_stream_load";

    [Fact]
    public async Task LoadCsvAsync_WithGzip_LoadsRowsIntoTable()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        string tableName = await CreateStreamLoadTableAsync().ConfigureAwait(true);
        try
        {
            using var client = new DotRocksStreamLoadClient(
                IntegrationTestEnvironment.ConnectionString
            );
            using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\n2,two\n3,three\n"));

            DotRocksStreamLoadResult result = await client
                .LoadCsvAsync(
                    StreamLoadDatabaseName,
                    tableName,
                    payload,
                    new DotRocksStreamLoadOptions
                    {
                        Label =
                            "dotrocks_"
                            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                        Columns = "id,value",
                        ColumnSeparator = ",",
                        RowDelimiter = "\\n",
                        Compression = DotRocksStreamLoadCompression.Gzip,
                    },
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            Assert.True(result.IsSuccess);
            Assert.Equal(3, result.NumberLoadedRows);
            Assert.Equal(3, await ReadRowCountAsync(tableName).ConfigureAwait(true));
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LoadCsvAsync_LoadsRowsIntoTable()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        string tableName = await CreateStreamLoadTableAsync().ConfigureAwait(true);
        try
        {
            using var client = new DotRocksStreamLoadClient(
                IntegrationTestEnvironment.ConnectionString
            );
            using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\n2,two\n"));

            DotRocksStreamLoadResult result = await client
                .LoadCsvAsync(
                    StreamLoadDatabaseName,
                    tableName,
                    payload,
                    new DotRocksStreamLoadOptions
                    {
                        Label =
                            "dotrocks_"
                            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                        Columns = "id,value",
                        ColumnSeparator = ",",
                        RowDelimiter = "\\n",
                    },
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            Assert.True(result.IsSuccess);
            Assert.Equal(2, result.NumberLoadedRows);
            Assert.Equal(2, await ReadRowCountAsync(tableName).ConfigureAwait(true));
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task TransactionalLoadCsvAsync_Commit_MakesRowsVisible()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        string tableName = await CreateStreamLoadTableAsync().ConfigureAwait(true);
        try
        {
            using var client = new DotRocksStreamLoadClient(
                IntegrationTestEnvironment.ConnectionString
            );
            DotRocksStreamLoadTransaction transaction = await client
                .BeginTransactionAsync(
                    StreamLoadDatabaseName,
                    tableName,
                    new DotRocksStreamLoadTransactionOptions
                    {
                        Label =
                            "dotrocks_tx_"
                            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                    },
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\n2,two\n"));
            DotRocksStreamLoadResult load = await transaction
                .LoadCsvAsync(
                    payload,
                    new DotRocksStreamLoadOptions
                    {
                        Columns = "id,value",
                        ColumnSeparator = ",",
                        RowDelimiter = "\\n",
                    },
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            DotRocksStreamLoadResult prepare = await transaction
                .PrepareAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            DotRocksStreamLoadResult commit = await transaction
                .CommitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.True(load.IsSuccess);
            Assert.True(prepare.IsSuccess);
            Assert.True(commit.IsSuccess);
            Assert.Equal(2, await ReadRowCountAsync(tableName).ConfigureAwait(true));
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task TransactionalLoadCsvAsync_Rollback_HidesRows()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        string tableName = await CreateStreamLoadTableAsync().ConfigureAwait(true);
        try
        {
            using var client = new DotRocksStreamLoadClient(
                IntegrationTestEnvironment.ConnectionString
            );
            DotRocksStreamLoadTransaction transaction = await client
                .BeginTransactionAsync(
                    StreamLoadDatabaseName,
                    tableName,
                    new DotRocksStreamLoadTransactionOptions
                    {
                        Label =
                            "dotrocks_tx_"
                            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                    },
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\n2,two\n"));
            DotRocksStreamLoadResult load = await transaction
                .LoadCsvAsync(
                    payload,
                    new DotRocksStreamLoadOptions
                    {
                        Columns = "id,value",
                        ColumnSeparator = ",",
                        RowDelimiter = "\\n",
                    },
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            DotRocksStreamLoadResult rollback = await transaction
                .RollbackAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.True(load.IsSuccess);
            Assert.True(rollback.IsSuccess);
            Assert.Equal(0, await ReadRowCountAsync(tableName).ConfigureAwait(true));
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task TransactionalLoadCsvAsync_FailedLoad_RejectsFurtherOperations()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        string tableName = await CreateStreamLoadTableAsync().ConfigureAwait(true);
        try
        {
            using var client = new DotRocksStreamLoadClient(
                IntegrationTestEnvironment.ConnectionString
            );
            DotRocksStreamLoadTransaction transaction = await client
                .BeginTransactionAsync(
                    StreamLoadDatabaseName,
                    tableName,
                    new DotRocksStreamLoadTransactionOptions
                    {
                        Label =
                            "dotrocks_tx_"
                            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                    },
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);

            using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\n"));
            await Assert
                .ThrowsAsync<DotRocksStreamLoadException>(async () =>
                    await transaction
                        .LoadCsvAsync(
                            tableName + "_missing",
                            payload,
                            new DotRocksStreamLoadOptions
                            {
                                Columns = "id,value",
                                ColumnSeparator = ",",
                                RowDelimiter = "\\n",
                            },
                            TestContext.Current.CancellationToken
                        )
                        .ConfigureAwait(true)
                )
                .ConfigureAwait(true);

            await Assert
                .ThrowsAsync<InvalidOperationException>(async () =>
                    await transaction
                        .PrepareAsync(TestContext.Current.CancellationToken)
                        .ConfigureAwait(true)
                )
                .ConfigureAwait(true);
            await Assert
                .ThrowsAsync<InvalidOperationException>(async () =>
                    await transaction
                        .RollbackAsync(TestContext.Current.CancellationToken)
                        .ConfigureAwait(true)
                )
                .ConfigureAwait(true);
            Assert.Equal(0, await ReadRowCountAsync(tableName).ConfigureAwait(true));
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<string> CreateStreamLoadTableAsync()
    {
        string tableName =
            "dotrocks_load_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand createDatabase = connection.CreateCommand();
        createDatabase.CommandText = $"CREATE DATABASE IF NOT EXISTS {StreamLoadDatabaseName}";
        await createDatabase
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        using DbCommand createTable = connection.CreateCommand();
        createTable.CommandText = $"""
            CREATE TABLE {StreamLoadDatabaseName}.{tableName}
            (
                id INT NOT NULL,
                value VARCHAR(32) NOT NULL
            )
            DUPLICATE KEY(id)
            DISTRIBUTED BY HASH(id) BUCKETS 1
            PROPERTIES ("replication_num" = "1")
            """;
        await createTable
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
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
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {StreamLoadDatabaseName}.{tableName}";
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<int> ReadRowCountAsync(string tableName)
    {
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {StreamLoadDatabaseName}.{tableName}";
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
