using System.Data;
using System.Diagnostics.CodeAnalysis;
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

        Assert.Equal("1", value);
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
        Assert.Equal("1", reader.GetString(0));
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
    [InlineData("SELECT 123", "123")]
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
}
