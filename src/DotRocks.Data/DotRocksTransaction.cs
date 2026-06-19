using System.Data;
using System.Data.Common;

namespace DotRocks.Data;

/// <summary>
/// Represents a StarRocks transaction associated with a <see cref="DotRocksConnection"/>.
/// </summary>
public sealed class DotRocksTransaction : DbTransaction
{
    private readonly DotRocksConnection _connection;
    private bool _isCompleted;

    internal DotRocksTransaction(DotRocksConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    /// <inheritdoc />
    public override IsolationLevel IsolationLevel { get; }

    /// <inheritdoc />
    protected override DbConnection? DbConnection => _isCompleted ? null : _connection;

    internal DotRocksConnection DotRocksConnection => _connection;

    internal bool IsCompleted => _isCompleted;

    /// <inheritdoc />
    public override void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await _connection
            .ExecuteTransactionCommandAsync("COMMIT WORK", this, cancellationToken)
            .ConfigureAwait(false);
        MarkCompleted();
    }

    /// <inheritdoc />
    public override void Rollback() =>
        RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await _connection
            .ExecuteTransactionCommandAsync("ROLLBACK WORK", this, cancellationToken)
            .ConfigureAwait(false);
        MarkCompleted();
    }

    internal void EnsureActive()
    {
        if (_isCompleted)
        {
            throw new InvalidOperationException("The DotRocks transaction has completed.");
        }
    }

    internal void MarkCompleted()
    {
        if (_isCompleted)
        {
            return;
        }

        _isCompleted = true;
        _connection.ClearActiveTransaction(this);
    }

    internal void MarkCompletedBecauseConnectionClosed()
    {
        _isCompleted = true;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isCompleted)
        {
            _connection.AbortTransaction(this);
            _isCompleted = true;
        }

        base.Dispose(disposing);
    }
}
