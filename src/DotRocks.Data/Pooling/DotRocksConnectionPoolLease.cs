namespace DotRocks.Data.Pooling;

internal sealed class DotRocksConnectionPoolLease : IDisposable
{
    private readonly DotRocksConnectionPool? _pool;
    private bool _isReturned;

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
        if (_isReturned)
        {
            return;
        }

        _isReturned = true;
        if (_pool is null)
        {
            PhysicalConnection.Dispose();
            return;
        }

        _pool.Return(PhysicalConnection, reusable);
    }

    public void Dispose() => Return(reusable: false);
}
