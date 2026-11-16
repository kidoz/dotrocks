using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

[Collection("StarRocks integration")]
public sealed class ReviewRegressionTests
{
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
