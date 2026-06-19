using System.Data;
using System.Data.Common;
using System.Globalization;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

[Collection("StarRocks integration")]
public sealed class DataSourceIntegrationTests
{
    [Fact]
    public async Task OpenConnectionAsync_AuthenticatesAgainstStarRocks()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        using var dataSource = new DotRocksDataSource(IntegrationTestEnvironment.ConnectionString);

        using DbConnection connection = await dataSource
            .OpenConnectionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.False(string.IsNullOrWhiteSpace(connection.ServerVersion));
    }

    [Fact]
    public async Task PooledConnections_FromDataSource_ReusePhysicalConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        DotRocksConnection.ClearAllPools();
        string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
        using var dataSource = new DotRocksDataSource(connectionString);
        long firstConnectionId;

        using (
            DbConnection first = await dataSource
                .OpenConnectionAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        )
        {
            firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);
            await first.CloseAsync().ConfigureAwait(true);
        }

        using (
            DbConnection second = await dataSource
                .OpenConnectionAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        )
        {
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

    private static async Task<long> ReadConnectionIdAsync(DbConnection connection)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CONNECTION_ID()";
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
