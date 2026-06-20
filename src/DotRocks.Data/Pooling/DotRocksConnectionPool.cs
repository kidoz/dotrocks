using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "Disposing the SemaphoreSlim strands pending WaitAsync waiters (deadlocking in-flight leases on ClearAllPools); it holds no unmanaged handle, so it is intentionally left undisposed."
    )]
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
        DotRocksConnectionPoolKey key = options.CreatePoolKey();
        while (true)
        {
            DotRocksConnectionPool pool = Pools.GetOrAdd(
                key,
                _ => new DotRocksConnectionPool(options)
            );
            if (!pool._isDisposed)
            {
                return pool;
            }

            // A concurrent ClearAllPools() disposed the cached pool; evict that exact instance
            // (only if it is still the cached one) and try again with a fresh pool.
            (
                (ICollection<KeyValuePair<DotRocksConnectionPoolKey, DotRocksConnectionPool>>)Pools
            ).Remove(
                new KeyValuePair<DotRocksConnectionPoolKey, DotRocksConnectionPool>(key, pool)
            );
        }
    }

    // Leases from the current pool for the options, transparently retrying when a concurrent
    // ClearAllPools()/Dispose() disposes the pool mid-lease so callers never observe the race.
    public static async ValueTask<DotRocksConnectionPoolLease> LeaseFromPoolAsync(
        DotRocksConnectionOptions options,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DotRocksConnectionPool pool = GetPool(options);
            try
            {
                return await pool.LeaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The pool was disposed between selection and lease; loop to a fresh pool.
            }
        }
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

        // Deliberately do NOT dispose _leaseGate. Disposing a SemaphoreSlim does not cancel
        // pending WaitAsync waiters — it strands them forever, which deadlocks any lease that is
        // in flight when ClearAllPools()/Dispose() runs. The semaphore holds no unmanaged handle
        // (AvailableWaitHandle is never used), so leaving it undisposed is safe; stranded waiters
        // instead complete normally as outstanding leases are returned.
    }

    private sealed record PooledPhysicalConnection(
        DotRocksPhysicalConnection Connection,
        DateTimeOffset ReturnedAt
    );
}
