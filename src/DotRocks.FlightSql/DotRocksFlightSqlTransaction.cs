using Apache.Arrow.Flight.Sql;

namespace DotRocks.FlightSql;

/// <summary>
/// Represents a Flight SQL transaction owned by a <see cref="DotRocksFlightSqlDataSource" />.
/// </summary>
/// <remarks>
/// A dispatched completion whose response is lost leaves the transaction in doubt. Reconcile its
/// outcome with the server before taking further action; the handle cannot be reused or rolled back.
/// </remarks>
public sealed class DotRocksFlightSqlTransaction : IAsyncDisposable
{
    private const int Active = 0;
    private const int Completing = 1;
    private const int Completed = 2;
    private const int InDoubt = 3;

    private readonly DotRocksFlightSqlDataSource _dataSource;
    private readonly Transaction _transaction;
    private int _state;

    internal DotRocksFlightSqlTransaction(
        DotRocksFlightSqlDataSource dataSource,
        Transaction transaction
    )
    {
        _dataSource = dataSource;
        _transaction = transaction;
    }

    /// <summary>
    /// Gets whether the server has confirmed completion of this transaction.
    /// </summary>
    public bool IsCompleted => Volatile.Read(ref _state) == Completed;

    /// <summary>
    /// Gets whether completion was dispatched without a confirmed outcome.
    /// </summary>
    public bool IsInDoubt => Volatile.Read(ref _state) == InDoubt;

    /// <summary>
    /// Executes a query in this transaction.
    /// </summary>
    public Task<DotRocksFlightSqlResult> ExecuteQueryAsync(
        string sql,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfNotActive();
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
        ThrowIfNotActive();
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
    /// <remarks>
    /// A failure after dispatch marks the transaction in doubt. Do not retry completion or attempt
    /// rollback; reconcile the outcome with the server.
    /// </remarks>
    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        CompleteAsync(commit: true, cancellationToken);

    /// <summary>
    /// Rolls back the transaction.
    /// </summary>
    /// <remarks>
    /// A failure after dispatch marks the transaction in doubt and prevents further operations.
    /// </remarks>
    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        CompleteAsync(commit: false, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _state, Completing, Active) != Active)
        {
            return;
        }

        bool dispatched = false;
        try
        {
            await _dataSource
                .RollbackTransactionAsync(
                    _transaction,
                    CancellationToken.None,
                    () => dispatched = true
                )
                .ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _state, dispatched ? InDoubt : Active);
            throw;
        }

        Volatile.Write(ref _state, Completed);
    }

    internal Transaction FlightTransaction
    {
        get
        {
            ThrowIfNotActive();
            return _transaction;
        }
    }

    private async Task CompleteAsync(bool commit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _state, Completing, Active) != Active)
        {
            throw new InvalidOperationException(
                "The Flight SQL transaction is completed, completing, or in doubt."
            );
        }

        bool dispatched = false;
        try
        {
            Task completion = commit
                ? _dataSource.CommitTransactionAsync(
                    _transaction,
                    cancellationToken,
                    () => dispatched = true
                )
                : _dataSource.RollbackTransactionAsync(
                    _transaction,
                    cancellationToken,
                    () => dispatched = true
                );
            await completion.ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _state, dispatched ? InDoubt : Active);
            throw;
        }

        Volatile.Write(ref _state, Completed);
    }

    private void ThrowIfNotActive()
    {
        if (IsInDoubt)
        {
            throw new InvalidOperationException(
                "The Flight SQL transaction outcome is in doubt; reconcile it with the server."
            );
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) != Active, this);
    }
}
