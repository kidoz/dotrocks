using Apache.Arrow.Flight.Sql;

namespace DotRocks.FlightSql;

/// <summary>
/// Represents a Flight SQL transaction owned by a <see cref="DotRocksFlightSqlDataSource" />.
/// </summary>
public sealed class DotRocksFlightSqlTransaction : IAsyncDisposable
{
    private readonly DotRocksFlightSqlDataSource _dataSource;
    private readonly Transaction _transaction;
    private int _completed;

    internal DotRocksFlightSqlTransaction(
        DotRocksFlightSqlDataSource dataSource,
        Transaction transaction
    )
    {
        _dataSource = dataSource;
        _transaction = transaction;
    }

    /// <summary>
    /// Executes a query in this transaction.
    /// </summary>
    public Task<DotRocksFlightSqlResult> ExecuteQueryAsync(
        string sql,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfCompleted();
        return _dataSource.ExecuteQueryAsync(
            sql,
            _transaction,
            commandTimeout: null,
            cancellationToken
        );
    }

    /// <summary>
    /// Executes an update in this transaction.
    /// </summary>
    public Task<long> ExecuteUpdateAsync(string sql, CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        return _dataSource.ExecuteUpdateAsync(
            sql,
            _transaction,
            commandTimeout: null,
            cancellationToken
        );
    }

    /// <summary>
    /// Commits the transaction.
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        BeginCompletion();
        await _dataSource
            .CommitTransactionAsync(_transaction, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back the transaction.
    /// </summary>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        BeginCompletion();
        await _dataSource
            .RollbackTransactionAsync(_transaction, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            await _dataSource
                .RollbackTransactionAsync(_transaction, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    internal Transaction FlightTransaction
    {
        get
        {
            ThrowIfCompleted();
            return _transaction;
        }
    }

    private void BeginCompletion()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            throw new InvalidOperationException("The Flight SQL transaction is already completed.");
        }
    }

    private void ThrowIfCompleted() => ObjectDisposedException.ThrowIf(_completed != 0, this);
}
