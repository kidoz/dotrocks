using System.Data;
using System.Data.Common;

namespace DotRocks.FlightSql;

/// <summary>
/// Represents an ADO.NET transaction executed through Arrow Flight SQL.
/// </summary>
/// <remarks>
/// Completion is recorded only after the server confirms it. A failed <see cref="CommitAsync" />
/// therefore leaves the transaction usable so that the caller can retry or roll back.
/// </remarks>
public sealed class DotRocksFlightSqlDbTransaction : DbTransaction
{
    private readonly DotRocksFlightSqlDbConnection _connection;
    private readonly DotRocksFlightSqlTransaction _transaction;
    private int _completed;

    internal DotRocksFlightSqlDbTransaction(
        DotRocksFlightSqlDbConnection connection,
        DotRocksFlightSqlTransaction transaction,
        IsolationLevel isolationLevel
    )
    {
        _connection = connection;
        _transaction = transaction;
        IsolationLevel = isolationLevel;
    }

    /// <inheritdoc />
    public override IsolationLevel IsolationLevel { get; }

    /// <inheritdoc />
    protected override DbConnection? DbConnection => _completed == 0 ? _connection : null;

    internal DotRocksFlightSqlTransaction FlightTransaction
    {
        get
        {
            EnsureActive();
            return _transaction;
        }
    }

    internal DotRocksFlightSqlDbConnection OwnerConnection => _connection;

    /// <inheritdoc />
    public override void Commit() =>
        throw new NotSupportedException(
            "Flight SQL transaction completion is asynchronous only. Use CommitAsync."
        );

    /// <inheritdoc />
    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        MarkCompleted();
    }

    /// <inheritdoc />
    public override void Rollback() =>
        throw new NotSupportedException(
            "Flight SQL transaction completion is asynchronous only. Use RollbackAsync."
        );

    /// <inheritdoc />
    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        MarkCompleted();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _completed == 0)
        {
            throw new NotSupportedException(
                "Dispose an active Flight SQL transaction asynchronously with DisposeAsync."
            );
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            try
            {
                await _transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // The connection must be released even when the rollback fails, otherwise it keeps
                // pointing at a transaction that can no longer be completed.
                _connection.ClearActiveTransaction(this);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void MarkCompleted()
    {
        Interlocked.Exchange(ref _completed, 1);
        _connection.ClearActiveTransaction(this);
    }

    private void EnsureActive()
    {
        if (_completed != 0)
        {
            throw new InvalidOperationException("The Flight SQL transaction has completed.");
        }
    }
}
