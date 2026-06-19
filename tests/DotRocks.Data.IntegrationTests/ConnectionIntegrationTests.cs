using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

public sealed class ConnectionIntegrationTests
{
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
                IntegrationTestEnvironment.ConnectionString
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

    private static async Task<long> ReadConnectionIdAsync(DotRocksConnection connection)
    {
        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CONNECTION_ID()";
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
