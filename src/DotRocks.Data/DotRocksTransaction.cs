using System.Data;
using System.Data.Common;
using System.Diagnostics;
using DotRocks.Data.Diagnostics;

namespace DotRocks.Data;

/// <summary>
/// Represents a StarRocks transaction associated with a <see cref="DotRocksConnection"/>.
/// </summary>
public sealed class DotRocksTransaction : DbTransaction
{
    private readonly DotRocksConnection _connection;
    private readonly long _startTimestamp;
    private bool _isCompleted;
    private bool _durationRecorded;

    internal DotRocksTransaction(DotRocksConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
        _startTimestamp = Stopwatch.GetTimestamp();
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
        RecordDuration("committed");
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
        RecordDuration("rolledback");
    }

    // Bounded label only: outcome in {committed, rolledback}. Recorded once per transaction.
    private void RecordDuration(string outcome)
    {
        if (_durationRecorded)
        {
            return;
        }

        _durationRecorded = true;
        DotRocksTelemetry.TransactionDuration.Record(
            Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds,
            new TagList { { "outcome", outcome } }
        );
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
            // Disposing an uncommitted transaction rolls it back and leaves the connection
            // usable, matching the DbTransaction contract (only a failed rollback aborts).
            _connection.RollbackTransactionForDispose(this);
            _isCompleted = true;
            RecordDuration("rolledback");
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_isCompleted)
        {
            await _connection.RollbackTransactionForDisposeAsync(this).ConfigureAwait(false);
            _isCompleted = true;
            RecordDuration("rolledback");
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
