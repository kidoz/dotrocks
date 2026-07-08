using System.Data.Common;
using System.Globalization;
using DotRocks.Data.Pooling;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

[Collection("StarRocks integration")]
public sealed class ConnectionPoolReapIntegrationTests
{
    [Fact]
    public async Task Eviction_WithOutstandingLease_DoesNotReapPool()
    {
        // The unit tests cover the dormant-pool reap path but cannot hold a real lease (that needs a
        // physical connection). This verifies the complementary reap/lease atomicity property
        // against a live server: an eviction cycle must not reap a pool that has an outstanding
        // lease, and the leased connection stays usable.
        IntegrationTestEnvironment.SkipUnlessEnabled();
        DotRocksConnection.ClearAllPools();
        try
        {
            string connectionString = BuildPoolingConnectionString();
            DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);

            using var connection = new DotRocksConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            // The open connection holds exactly one lease, so the pool is not dormant.
            DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
            Assert.Equal(1, pool.ActiveLeaseCount);

            // An eviction cycle must not reap a pool with an outstanding lease: GetPool returns the
            // same registered instance rather than a fresh one.
            pool.RunEvictionCycleForTests();
            Assert.Same(pool, DotRocksConnectionPool.GetPool(options));

            // The leased connection remains usable through the eviction.
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT 1";
                object? value = await command
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Assert.Equal(1L, Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }

            // Returning the connection drops the lease; the pool becomes eligible for reaping once
            // it also has no idle connections (the dormant path the unit tests cover).
            await connection.CloseAsync().ConfigureAwait(true);
            Assert.Equal(0, pool.ActiveLeaseCount);
        }
        finally
        {
            DotRocksConnection.ClearAllPools();
        }
    }

    private static string BuildPoolingConnectionString()
    {
        var builder = new DotRocksConnectionStringBuilder(
            IntegrationTestEnvironment.ConnectionString
        )
        {
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = 2,
            ConnectionIdleTimeout = 300,
        };

        return builder.ConnectionString;
    }
}
