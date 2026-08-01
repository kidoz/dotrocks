using Apache.Arrow.Flight.Sql;

namespace DotRocks.FlightSql;

/// <summary>
/// Represents a Flight SQL transaction owned by a <see cref="DotRocksFlightSqlDataSource" />.
/// </summary>
/// <remarks>
/// A transaction is marked completed only after the server confirms completion, so a failed commit
/// or rollback leaves it active and still recoverable.
/// </remarks>
public sealed class DotRocksFlightSqlTransaction : IAsyncDisposable
{
    private const int Active = 0;
    private const int Completing = 1;
    private const int Completed = 2;

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
    /// The transaction stays active when the server does not confirm the commit, so the caller can
    /// retry or roll back.
    /// </remarks>
    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        CompleteAsync(commit: true, cancellationToken);

    /// <summary>
    /// Rolls back the transaction.
    /// </summary>
    /// <remarks>
    /// The transaction stays active when the server does not confirm the rollback, so the caller
    /// can retry.
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

        try
        {
            await _dataSource
                .RollbackTransactionAsync(_transaction, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            // Disposal is the last chance to reach the server; a failed rollback still ends the
            // client-side transaction rather than leaving an unusable handle behind.
            Volatile.Write(ref _state, Completed);
        }
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
        if (Interlocked.CompareExchange(ref _state, Completing, Active) != Active)
        {
            throw new InvalidOperationException(
                "The Flight SQL transaction is already completed or completing."
            );
        }

        try
        {
            Task completion = commit
                ? _dataSource.CommitTransactionAsync(_transaction, cancellationToken)
                : _dataSource.RollbackTransactionAsync(_transaction, cancellationToken);
            await completion.ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _state, Active);
            throw;
        }

        Volatile.Write(ref _state, Completed);
    }

    private void ThrowIfNotActive() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) != Active, this);
}
