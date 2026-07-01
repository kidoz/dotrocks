using DotRocks.Data.Loading;
using DotRocks.Data.Pooling;
using Xunit;

namespace DotRocks.Protocol.Tests.Pooling;

public sealed class DotRocksConnectionPoolTests
{
    [Fact]
    public async Task GetPool_ConcurrentCallsForSameOptions_ReturnOneRegisteredInstance()
    {
        // Regression for the GetOrAdd creation race: every racing caller must converge on the
        // single registered pool, and losing instances are disposed (their eviction timers would
        // otherwise root them and keep firing forever).
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=pool-race.local;User ID=alice;Pooling=true"
        );
        try
        {
            using var startGate = new ManualResetEventSlim(initialState: false);
            Task<DotRocksConnectionPool>[] tasks =
            [
                .. Enumerable
                    .Range(0, 16)
                    .Select(_ =>
                        Task.Run(() =>
                        {
                            startGate.Wait(TestContext.Current.CancellationToken);
                            return DotRocksConnectionPool.GetPool(options);
                        })
                    ),
            ];
            startGate.Set();
            DotRocksConnectionPool[] pools = await Task.WhenAll(tasks)
                .WaitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.All(pools, pool => Assert.Same(pools[0], pool));
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
        }
    }

    [Fact]
    public void GetPool_AfterClear_ReturnsFreshUndisposedPool()
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=pool-clear.local;User ID=alice;Pooling=true"
        );
        try
        {
            DotRocksConnectionPool first = DotRocksConnectionPool.GetPool(options);
            DotRocksConnectionPool.Clear(options);

            DotRocksConnectionPool second = DotRocksConnectionPool.GetPool(options);

            Assert.NotSame(first, second);
            Assert.Equal(0, second.IdleCount);
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
        }
    }
}
