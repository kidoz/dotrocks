namespace DotRocks.Data.Pooling;

internal sealed class DotRocksConnectionPoolLease : IDisposable
{
    private readonly DotRocksConnectionPool? _pool;
    private int _isReturned;

    private DotRocksConnectionPoolLease(
        DotRocksPhysicalConnection physicalConnection,
        DotRocksConnectionPool? pool
    )
    {
        PhysicalConnection = physicalConnection;
        _pool = pool;
    }

    public DotRocksPhysicalConnection PhysicalConnection { get; }

    public static DotRocksConnectionPoolLease Unpooled(
        DotRocksPhysicalConnection physicalConnection
    ) => new(physicalConnection, pool: null);

    public static DotRocksConnectionPoolLease Pooled(
        DotRocksPhysicalConnection physicalConnection,
        DotRocksConnectionPool pool
    ) => new(physicalConnection, pool);

    public void Return(bool reusable)
    {
        // Close and Abort can both observe one lease. A second return releases the pool permit
        // twice or disposes a socket the pool has already handed out again.
        if (Interlocked.CompareExchange(ref _isReturned, 1, 0) != 0)
        {
            return;
        }

        if (_pool is null)
        {
            PhysicalConnection.Dispose();
            return;
        }

        _pool.Return(PhysicalConnection, reusable);
    }

    public void Dispose() => Return(reusable: false);
}
