using System.Collections.Concurrent;
using DotRocks.Data.Loading;

namespace DotRocks.Data.Pooling;

internal sealed class DotRocksConnectionPool : IDisposable
{
    private static readonly ConcurrentDictionary<
        DotRocksConnectionPoolKey,
        DotRocksConnectionPool
    > Pools = new();

    private readonly object _gate = new();
    private readonly DotRocksConnectionOptions _options;
    private readonly SemaphoreSlim _leaseGate;
    private readonly Queue<PooledPhysicalConnection> _idleConnections = new();
    private bool _isDisposed;

    private DotRocksConnectionPool(DotRocksConnectionOptions options)
    {
        _options = options;
        _leaseGate = new SemaphoreSlim(options.MaximumPoolSize, options.MaximumPoolSize);
    }

    public static DotRocksConnectionPool GetPool(DotRocksConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Pools.GetOrAdd(options.CreatePoolKey(), _ => new DotRocksConnectionPool(options));
    }

    public async ValueTask<DotRocksConnectionPoolLease> LeaseAsync(
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DotRocksPhysicalConnection? physicalConnection = TryTakeIdleConnection();
            physicalConnection ??= await DotRocksPhysicalConnection
                .OpenAsync(_options, cancellationToken)
                .ConfigureAwait(false);
            return DotRocksConnectionPoolLease.Pooled(physicalConnection, this);
        }
        catch
        {
            _leaseGate.Release();
            throw;
        }
    }

    public void Return(DotRocksPhysicalConnection physicalConnection, bool reusable)
    {
        ArgumentNullException.ThrowIfNull(physicalConnection);

        try
        {
            if (_isDisposed)
            {
                physicalConnection.Dispose();
                return;
            }

            if (!reusable || !physicalConnection.IsReusable)
            {
                physicalConnection.Dispose();
                return;
            }

            lock (_gate)
            {
                PruneExpiredIdleConnections();
                _idleConnections.Enqueue(
                    new PooledPhysicalConnection(physicalConnection, DateTimeOffset.UtcNow)
                );
            }
        }
        finally
        {
            if (!_isDisposed)
            {
                _leaseGate.Release();
            }
        }
    }

    internal static void ClearAll()
    {
        foreach (DotRocksConnectionPool pool in Pools.Values)
        {
            pool.Dispose();
        }

        Pools.Clear();
    }

    internal int IdleCount
    {
        get
        {
            lock (_gate)
            {
                return _idleConnections.Count;
            }
        }
    }

    private DotRocksPhysicalConnection? TryTakeIdleConnection()
    {
        lock (_gate)
        {
            PruneExpiredIdleConnections();
            while (_idleConnections.TryDequeue(out PooledPhysicalConnection? pooled))
            {
                if (pooled.Connection.IsReusable)
                {
                    return pooled.Connection;
                }

                pooled.Connection.Dispose();
            }
        }

        return null;
    }

    private void PruneExpiredIdleConnections()
    {
        if (_idleConnections.Count <= _options.MinimumPoolSize)
        {
            return;
        }

        int retained = _idleConnections.Count;
        var retainedConnections = new Queue<PooledPhysicalConnection>(_idleConnections.Count);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        while (_idleConnections.TryDequeue(out PooledPhysicalConnection? pooled))
        {
            bool expired =
                retained > _options.MinimumPoolSize
                && now - pooled.ReturnedAt >= _options.ConnectionIdleTimeout;
            if (expired || !pooled.Connection.IsReusable)
            {
                pooled.Connection.Dispose();
                retained--;
                continue;
            }

            retainedConnections.Enqueue(pooled);
        }

        while (retainedConnections.TryDequeue(out PooledPhysicalConnection? pooled))
        {
            _idleConnections.Enqueue(pooled);
        }
    }

    private void Clear()
    {
        lock (_gate)
        {
            while (_idleConnections.TryDequeue(out PooledPhysicalConnection? pooled))
            {
                pooled.Connection.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Clear();
        _leaseGate.Dispose();
    }

    private sealed record PooledPhysicalConnection(
        DotRocksPhysicalConnection Connection,
        DateTimeOffset ReturnedAt
    );
}
