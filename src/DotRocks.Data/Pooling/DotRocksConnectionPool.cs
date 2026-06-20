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
    private readonly Timer? _evictionTimer;
    private volatile bool _isDisposed;

    private DotRocksConnectionPool(DotRocksConnectionOptions options)
    {
        _options = options;
        _leaseGate = new SemaphoreSlim(options.MaximumPoolSize, options.MaximumPoolSize);

        // Periodically prune connections that have been idle past ConnectionIdleTimeout even when
        // the pool sees no rent/return activity. Without this an idle pool keeps sockets open.
        if (options.ConnectionIdleTimeout > TimeSpan.Zero)
        {
            _evictionTimer = new Timer(
                static state => ((DotRocksConnectionPool)state!).EvictExpiredIdleConnections(),
                this,
                options.ConnectionIdleTimeout,
                options.ConnectionIdleTimeout
            );
        }
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
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            DotRocksPhysicalConnection? physicalConnection = TryTakeIdleConnection();
            physicalConnection ??= await DotRocksPhysicalConnection
                .OpenAsync(_options, cancellationToken)
                .ConfigureAwait(false);
            return DotRocksConnectionPoolLease.Pooled(physicalConnection, this);
        }
        catch
        {
            ReleaseLease();
            throw;
        }
    }

    public void Return(DotRocksPhysicalConnection physicalConnection, bool reusable)
    {
        ArgumentNullException.ThrowIfNull(physicalConnection);

        try
        {
            // The decision to keep or discard, and the disposed-state check, must be made under
            // the gate together with the enqueue so a concurrent Dispose() cannot drain the idle
            // queue between the check and the enqueue (which would leak the connection).
            bool enqueued = false;
            if (reusable && physicalConnection.IsReusable)
            {
                lock (_gate)
                {
                    if (!_isDisposed)
                    {
                        PruneExpiredIdleConnections();
                        _idleConnections.Enqueue(
                            new PooledPhysicalConnection(physicalConnection, DateTimeOffset.UtcNow)
                        );
                        enqueued = true;
                    }
                }
            }

            if (!enqueued)
            {
                physicalConnection.Dispose();
            }
        }
        finally
        {
            // Always return the permit taken by the matching LeaseAsync. A disposed pool's
            // permit accounting no longer matters, so tolerate a disposed semaphore.
            ReleaseLease();
        }
    }

    private void ReleaseLease()
    {
        try
        {
            _leaseGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // The pool was disposed while a lease was outstanding; the permit count is moot.
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

    private void EvictExpiredIdleConnections()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            PruneExpiredIdleConnections();
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

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            while (_idleConnections.TryDequeue(out PooledPhysicalConnection? pooled))
            {
                pooled.Connection.Dispose();
            }
        }

        _evictionTimer?.Dispose();
        _leaseGate.Dispose();
    }

    private sealed record PooledPhysicalConnection(
        DotRocksPhysicalConnection Connection,
        DateTimeOffset ReturnedAt
    );
}
