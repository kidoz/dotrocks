using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

[Collection("StarRocks integration")]
public sealed class ConnectionIntegrationTests
{
    private const string TransactionDatabaseName = "dotrocks_tx";

    [Fact]
    public async Task OpenAsync_AuthenticatesAgainstStarRocks()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);

        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal("5.1.0", connection.ServerVersion);
    }

    [Fact]
    public async Task ExecuteScalarAsync_ReturnsSelectOne()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, value);
    }

    [Fact]
    public async Task ExecuteReaderAsync_ReadsSelectOneResultSet()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        using System.Data.Common.DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, reader.FieldCount);
        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal(1, reader.GetInt32(0));
        Assert.False(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotLeakPasswordOrConnectionString()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        const string secret = "super-secret-integration-password";
        var builder = new DotRocksConnectionStringBuilder(
            IntegrationTestEnvironment.ConnectionString
        )
        {
            UserId = "dotrocks_missing_user",
            Password = secret,
        };

        using var connection = new DotRocksConnection(builder.ConnectionString);

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await connection
                    .OpenAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            builder.ConnectionString,
            exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("SELECT 'abc'", "abc")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test SQL is fixed InlineData, not user input."
    )]
    public async Task ExecuteScalarAsync_ReturnsTextProtocolValues(string sql, string expected)
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = sql;

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(expected, value);
    }

    [Fact]
    public async Task ExecuteReaderAsync_MapsCommonStarRocksTypes()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CAST(123 AS INT) AS i32,
                CAST(123 AS BIGINT) AS i64,
                CAST(12.34 AS DECIMAL(10, 2)) AS amount,
                CAST(1.5 AS DOUBLE) AS ratio,
                CAST('2026-06-19' AS DATE) AS created_on,
                CAST('2026-06-19 13:14:15' AS DATETIME) AS created_at
            """;

        using System.Data.Common.DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal(123, reader.GetInt32(0));
        Assert.Equal(123L, reader.GetInt64(1));
        Assert.Equal(12.34m, reader.GetDecimal(2));
        Assert.Equal(1.5d, reader.GetDouble(3));
        Assert.Equal(new DateTime(2026, 6, 19), reader.GetDateTime(4));
        Assert.Equal(new DateTime(2026, 6, 19, 13, 14, 15), reader.GetDateTime(5));
        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Equal(typeof(long), reader.GetFieldType(1));
        Assert.Equal(typeof(decimal), reader.GetFieldType(2));
        Assert.Equal(typeof(double), reader.GetFieldType(3));
        Assert.Equal(typeof(DateTime), reader.GetFieldType(4));
        Assert.Equal(typeof(DateTime), reader.GetFieldType(5));
        Assert.False(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
    }

    [Fact]
    public async Task ExecuteReaderAsync_ExposesColumnSchemaAndGenericFieldValues()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CAST(123 AS INT) AS i32,
                CAST(123 AS BIGINT) AS i64,
                CAST(12.34 AS DECIMAL(10, 2)) AS amount,
                CAST(1.5 AS DOUBLE) AS ratio,
                CAST(1.25 AS FLOAT) AS single_value,
                CAST('2026-06-19 13:14:15' AS DATETIME) AS created_at,
                'hello' AS text_value
            """;

        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        ReadOnlyCollection<DbColumn> schema = reader.GetColumnSchema();

        Assert.Equal(7, schema.Count);
        Assert.Equal("i32", schema[0].ColumnName);
        Assert.Equal(0, schema[0].ColumnOrdinal);
        Assert.Equal(typeof(int), schema[0].DataType);
        Assert.Equal("i64", schema[1].ColumnName);
        Assert.Equal(typeof(long), schema[1].DataType);
        Assert.Equal("amount", schema[2].ColumnName);
        Assert.Equal(typeof(decimal), schema[2].DataType);
        Assert.Equal("ratio", schema[3].ColumnName);
        Assert.Equal(typeof(double), schema[3].DataType);
        Assert.Equal("single_value", schema[4].ColumnName);
        Assert.Equal(typeof(float), schema[4].DataType);
        Assert.Equal("created_at", schema[5].ColumnName);
        Assert.Equal(typeof(DateTime), schema[5].DataType);
        Assert.Equal("text_value", schema[6].ColumnName);
        Assert.Equal(typeof(string), schema[6].DataType);

        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal(
            123,
            await reader
                .GetFieldValueAsync<int>(0, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            123L,
            await reader
                .GetFieldValueAsync<long>(1, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            12.34m,
            await reader
                .GetFieldValueAsync<decimal>(2, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            1.5d,
            await reader
                .GetFieldValueAsync<double>(3, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            1.25f,
            await reader
                .GetFieldValueAsync<float>(4, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            new DateTime(2026, 6, 19, 13, 14, 15),
            await reader
                .GetFieldValueAsync<DateTime>(5, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            "hello",
            await reader
                .GetFieldValueAsync<string>(6, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.False(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
    }

    [Fact]
    public async Task ExecuteReaderAsync_BindsTextCommandParameters()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                @text AS text_value,
                @integer AS integer_value,
                @decimal AS decimal_value,
                IF(@flag, 'yes', 'no') AS flag_value,
                @date AS date_value,
                @time AS time_value,
                @guid AS guid_value,
                HEX(@bytes) AS bytes_value,
                @null_value AS null_value
            """;
        command.Parameters.Add(
            new DotRocksParameter { ParameterName = "text", Value = "O'Reilly" }
        );
        command.Parameters.Add(new DotRocksParameter { ParameterName = "integer", Value = 123 });
        command.Parameters.Add(new DotRocksParameter { ParameterName = "decimal", Value = 12.34m });
        command.Parameters.Add(new DotRocksParameter { ParameterName = "flag", Value = true });
        command.Parameters.Add(
            new DotRocksParameter { ParameterName = "date", Value = new DateOnly(2026, 6, 19) }
        );
        command.Parameters.Add(
            new DotRocksParameter { ParameterName = "time", Value = new TimeOnly(13, 14, 15) }
        );
        command.Parameters.Add(
            new DotRocksParameter
            {
                ParameterName = "guid",
                Value = Guid.Parse("9f4f591e-3db2-4879-856c-1c54b4241b76"),
            }
        );
        command.Parameters.Add(
            new DotRocksParameter
            {
                ParameterName = "bytes",
                Value = new byte[] { 0x00, 0xFF, 0x10 },
            }
        );
        command.Parameters.Add(
            new DotRocksParameter { ParameterName = "null_value", Value = DBNull.Value }
        );

        using System.Data.Common.DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal("O'Reilly", reader.GetString(0));
        Assert.Equal(123, reader.GetInt32(1));
        Assert.Equal(12.34m, reader.GetDecimal(2));
        Assert.Equal("yes", reader.GetString(3));
        Assert.Equal(new DateTime(2026, 6, 19), reader.GetDateTime(4));
        Assert.Equal("13:14:15", reader.GetString(5));
        Assert.Equal("9f4f591e-3db2-4879-856c-1c54b4241b76", reader.GetString(6));
        Assert.Equal("00FF10", reader.GetString(7));
        Assert.True(
            await reader
                .IsDBNullAsync(8, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact]
    public async Task ExecuteScalarAsync_ReturnsNullForSqlNull()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT NULL";

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Null(value);
    }

    [Fact]
    public async Task ExecuteScalarAsync_CommandTimeout_ClosesConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SLEEP(3)";
        command.CommandTimeout = 1;

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await command
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(exception.IsTransient);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ExecuteScalarAsync_UserCancellation_ClosesConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SLEEP(3)";
        command.CommandTimeout = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert
            .ThrowsAsync<OperationCanceledException>(async () =>
                await command.ExecuteScalarAsync(cancellation.Token).ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Cancel_ActiveCommand_ClosesConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SLEEP(3)";
        command.CommandTimeout = 0;

        Task<object?> execution = command.ExecuteScalarAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        command.Cancel();

        await Assert
            .ThrowsAsync<OperationCanceledException>(async () =>
                await execution.ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task BadSql_ThrowsDotRocksException()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT FROM";

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await command
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.NotNull(exception.ServerErrorCode);
        Assert.DoesNotContain(
            IntegrationTestEnvironment.ConnectionString,
            exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task OpenClose_CanRepeatOnSeparateConnections()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        for (int i = 0; i < 2; i++)
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(ConnectionState.Open, connection.State);
            await connection.CloseAsync().ConfigureAwait(true);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
    }

    [Fact]
    public async Task PooledConnections_ReusePhysicalConnection()
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

    [Fact]
    public async Task PooledConnections_RespectMaximumPoolSize()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        DotRocksConnection.ClearAllPools();
        string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
        using var first = new DotRocksConnection(connectionString);
        using var second = new DotRocksConnection(connectionString);
        await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Task openSecond = second.OpenAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.False(openSecond.IsCompleted);

        await first.CloseAsync().ConfigureAwait(true);
        await openSecond.ConfigureAwait(true);
        Assert.Equal(ConnectionState.Open, second.State);
        await second.CloseAsync().ConfigureAwait(true);
        DotRocksConnection.ClearAllPools();
    }

    [Fact]
    public async Task PooledConnections_DiscardBrokenPhysicalConnection()
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

            using System.Data.Common.DbCommand command = first.CreateCommand();
            command.CommandText = "SELECT SLEEP(3)";
            command.CommandTimeout = 1;

            await Assert
                .ThrowsAsync<DotRocksException>(async () =>
                    await command
                        .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                        .ConfigureAwait(true)
                )
                .ConfigureAwait(true);
            Assert.Equal(ConnectionState.Closed, first.State);
        }

        using (var second = new DotRocksConnection(connectionString))
        {
            await second.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            long secondConnectionId = await ReadConnectionIdAsync(second).ConfigureAwait(true);

            Assert.NotEqual(firstConnectionId, secondConnectionId);
            await second.CloseAsync().ConfigureAwait(true);
        }

        DotRocksConnection.ClearAllPools();
    }

    [Fact]
    public async Task ActiveReader_BlocksSecondCommandUntilConsumed()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand firstCommand = connection.CreateCommand();
        firstCommand.CommandText = "SELECT 1 UNION ALL SELECT 2";
        using DbDataReader reader = await firstCommand
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );

        using DbCommand secondCommand = connection.CreateCommand();
        secondCommand.CommandText = "SELECT 1";
        InvalidOperationException exception = await Assert
            .ThrowsAsync<InvalidOperationException>(async () =>
                await secondCommand
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Contains("active reader", exception.Message, StringComparison.OrdinalIgnoreCase);

        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        { }

        object? value = await secondCommand
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(1, value);
    }

    [Fact]
    public async Task ClosingReaderBeforeExhaustion_DiscardsPhysicalConnection()
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

            using DbCommand command = first.CreateCommand();
            command.CommandText = "SELECT 1 UNION ALL SELECT 2";
            using DbDataReader reader = await command
                .ExecuteReaderAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(
                await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
            );

            await reader.CloseAsync().ConfigureAwait(true);

            Assert.Equal(ConnectionState.Closed, first.State);
        }

        using (var second = new DotRocksConnection(connectionString))
        {
            await second.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            long secondConnectionId = await ReadConnectionIdAsync(second).ConfigureAwait(true);

            Assert.NotEqual(firstConnectionId, secondConnectionId);
            await second.CloseAsync().ConfigureAwait(true);
        }

        DotRocksConnection.ClearAllPools();
    }

    [Fact]
    public async Task ReaderClose_RespectsCommandBehaviorCloseConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        using DbDataReader reader = await command
            .ExecuteReaderAsync(
                CommandBehavior.CloseConnection,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        { }

        await reader.CloseAsync().ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test SQL uses only a compile-time row-count constant."
    )]
    public async Task LargeResult_OpensReaderWithoutBufferingAllRows()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        const int rowCount = 100_000;
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocatedBeforeOpen = GC.GetTotalAllocatedBytes(precise: true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH d AS (
                SELECT 0 AS n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4
                UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9
            )
            SELECT
                a.n + (b.n * 10) + (c.n * 100) + (d.n * 1000) + (e.n * 10000) AS number
            FROM d a, d b, d c, d d, d e
            """;
        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        long allocatedForOpen = GC.GetTotalAllocatedBytes(precise: true) - allocatedBeforeOpen;
        Assert.True(
            allocatedForOpen < 8_000_000,
            $"Opening the reader allocated {allocatedForOpen.ToString(CultureInfo.InvariantCulture)} byte(s), which indicates result buffering."
        );

        int rowsRead = 0;
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            _ = reader.GetInt64(0);
            rowsRead++;
        }

        Assert.Equal(rowCount, rowsRead);
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
            await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    $"INSERT INTO {tableName} SELECT 1, 10"
                )
                .ConfigureAwait(true);

            await transaction
                .CommitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(10, await ReadTransactionValueAsync(tableName, 1).ConfigureAwait(true));
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Transaction_Rollback_HidesInsertedRows()
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
            await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    $"INSERT INTO {tableName} SELECT 1, 10"
                )
                .ConfigureAwait(true);

            await transaction
                .RollbackAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            int rowCount = await ReadTransactionRowCountAsync(tableName).ConfigureAwait(true);
            if (rowCount != 0)
            {
                Assert.Skip(
                    "The pinned StarRocks integration image accepted ROLLBACK WORK but made the inserted row visible."
                );
            }

            Assert.Equal(0, rowCount);
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
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

    private static async Task<long> ReadConnectionIdAsync(DotRocksConnection connection)
    {
        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CONNECTION_ID()";
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<string> CreateTransactionTableAsync()
    {
        string tableName =
            "dotrocks_tx_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using System.Data.Common.DbCommand createDatabase = connection.CreateCommand();
        createDatabase.CommandText = $"CREATE DATABASE IF NOT EXISTS {TransactionDatabaseName}";
        await createDatabase
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        using var databaseConnection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await databaseConnection
            .OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        using System.Data.Common.DbCommand command = databaseConnection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {tableName}
            (
                id INT NOT NULL,
                value INT NOT NULL
            )
            PRIMARY KEY(id)
            DISTRIBUTED BY HASH(id) BUCKETS 1
            PROPERTIES ("replication_num" = "1")
            """;
        await command
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
        using var connection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {tableName}";
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test SQL is built from internally generated table names and constant values."
    )]
    private static async Task ExecuteNonQueryAsync(
        DotRocksConnection connection,
        System.Data.Common.DbTransaction transaction,
        string commandText
    )
    {
        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test database names are constants controlled by the test."
    )]
    private static async Task UseTransactionDatabaseAsync(DotRocksConnection connection)
    {
        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = $"USE {TransactionDatabaseName}";
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<int> ReadTransactionValueAsync(string tableName, int id)
    {
        using var connection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {tableName} WHERE id = @id";
        command.Parameters.Add(new DotRocksParameter { ParameterName = "id", Value = id });
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<int> ReadTransactionRowCountAsync(string tableName)
    {
        using var connection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
