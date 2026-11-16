using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

[Collection("StarRocks integration")]
public sealed class ReviewRegressionTests
{
    [Theory]
    [InlineData(DotRocksParameterMode.Auto)]
    [InlineData(DotRocksParameterMode.ServerPrepared)]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Both branches are fixed test SQL literals."
    )]
    public async Task YearZero_IsAnUnsupportedValueWithoutBreakingTheConnection(
        DotRocksParameterMode mode
    )
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();
        CancellationToken token = TestContext.Current.CancellationToken;
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(true);
        using var command = connection.CreateCommand();
        command.ParameterMode = mode;
        command.CommandText =
            mode == DotRocksParameterMode.ServerPrepared
                ? "SELECT CAST('0000-01-01 12:00:00' AS DATETIME), CAST('0000-01-01 12:00:00' AS DATETIME), 7"
                : "SELECT CAST('0000-02-29' AS DATE), CAST('0000-01-01 12:00:00' AS DATETIME), 7";
        using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(true))
        {
            Assert.True(await reader.ReadAsync(token).ConfigureAwait(true));
            Assert.False(await reader.IsDBNullAsync(0, token).ConfigureAwait(true));
            Assert.Throws<DotRocksUnsupportedFeatureException>(() => reader.GetValue(0));
            Assert.Throws<DotRocksUnsupportedFeatureException>(() => reader.GetDateTime(1));
            Assert.Equal(
                7,
                Convert.ToInt32(
                    reader.GetValue(2),
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );
        }
        command.CommandText = "SELECT 1";
        Assert.Equal(
            1,
            Convert.ToInt32(
                await command.ExecuteScalarAsync(token).ConfigureAwait(true),
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoubleQuotedEscapes_PreserveLiteralAndParameterBoundaries(bool prepare)
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();
        CancellationToken token = TestContext.Current.CancellationToken;
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(true);
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT "a\"b @p", "a""b", "a\\", @p""";
        const string value = "\" OR TRUE -- ";
        command.Parameters.Add(new DotRocksParameter { ParameterName = "p", Value = value });
        if (prepare)
        {
            await command.PrepareAsync(token).ConfigureAwait(true);
        }
        using DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(token).ConfigureAwait(true));
        Assert.Equal("a\"b @p", reader.GetString(0));
        Assert.Equal("a\"b", reader.GetString(1));
        Assert.Equal("a\\", reader.GetString(2));
        Assert.Equal(value, reader.GetString(3));
        Assert.False(await reader.ReadAsync(token).ConfigureAwait(true));
    }

    [Fact]
    public async Task TemporaryTable_DoesNotCrossPooledLeases()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();
        CancellationToken token = TestContext.Current.CancellationToken;
        var builder = new DotRocksConnectionStringBuilder(
            IntegrationTestEnvironment.ConnectionString
        )
        {
            Database = StarRocksIntegrationDatabaseFixture.TransactionDatabaseName,
            Pooling = true,
            MaximumPoolSize = 1,
        };
        using var connection = new DotRocksConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(true);
            object? firstSession = await ScalarAsync(connection, "SELECT CONNECTION_ID()", token)
                .ConfigureAwait(true);
            await ScalarAsync(
                    connection,
                    "CREATE TEMPORARY TABLE review_session_table (id INT) DISTRIBUTED BY HASH(id) BUCKETS 1 PROPERTIES ('replication_num'='1')",
                    token
                )
                .ConfigureAwait(true);
            await connection.CloseAsync().ConfigureAwait(true);
            await connection.OpenAsync(token).ConfigureAwait(true);
            Assert.NotEqual(
                firstSession,
                await ScalarAsync(connection, "SELECT CONNECTION_ID()", token).ConfigureAwait(true)
            );
            await Assert
                .ThrowsAsync<DotRocksException>(() =>
                    ScalarAsync(connection, "SELECT COUNT(*) FROM review_session_table", token)
                )
                .ConfigureAwait(true);
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(true);
            DotRocksConnection.ClearPool(connection);
        }
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed SQL from these integration tests reaches this helper."
    )]
    private static async Task<object?> ScalarAsync(
        DotRocksConnection connection,
        string sql,
        CancellationToken token
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(token).ConfigureAwait(true);
    }
}
