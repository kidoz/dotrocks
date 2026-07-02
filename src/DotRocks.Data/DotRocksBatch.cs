using System.Data;
using System.Data.Common;
using DotRocks.Data.Protocol.Commands;
using DotRocks.Data.Protocol.Results;

namespace DotRocks.Data;

/// <summary>
/// Executes a batch of StarRocks commands.
/// </summary>
/// <remarks>
/// StarRocks executes one text command per round trip, so a batch is executed as the sequence
/// of its commands; there is no protocol-level single-round-trip batching. Commands run in
/// order and <see cref="DbDataReader.NextResult"/> advances across their result sets.
/// </remarks>
public sealed class DotRocksBatch : DbBatch
{
    private readonly ActiveOperationGate _activeOperationGate = new();
    private readonly DotRocksBatchCommandCollection _batchCommands = new();
    private DotRocksConnection? _connection;
    private DbTransaction? _transaction;
    private int _timeout = 30;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksBatch"/> class.
    /// </summary>
    public DotRocksBatch() { }

    internal DotRocksBatch(DotRocksConnection connection)
    {
        _connection = connection;
    }

    /// <inheritdoc />
    public override int Timeout
    {
        get => _timeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _timeout = value;
        }
    }

    /// <inheritdoc />
    protected override DbBatchCommandCollection DbBatchCommands => _batchCommands;

    /// <inheritdoc />
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (value is null)
            {
                _connection = null;
                return;
            }

            _connection =
                value as DotRocksConnection
                ?? throw new InvalidOperationException(
                    "DotRocksBatch requires a DotRocksConnection."
                );
        }
    }

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    /// <inheritdoc />
    public override void Cancel()
    {
        if (_activeOperationGate.TryCancelActiveOperation())
        {
            _connection?.Abort();
        }
    }

    /// <inheritdoc />
    protected override DbBatchCommand CreateDbBatchCommand() => new DotRocksBatchCommand();

    /// <inheritdoc />
    public override void Prepare() =>
        throw new NotSupportedException("DotRocks batches do not support prepared execution.");

    /// <inheritdoc />
    public override Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("DotRocks batches do not support prepared execution.");
    }

    /// <inheritdoc />
    public override int ExecuteNonQuery() =>
        ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<QueryResult> results = await ExecuteAllAsync(cancellationToken)
            .ConfigureAwait(false);
        long total = QueryResult.SumRecordsAffected(results);
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <inheritdoc />
    public override object? ExecuteScalar() =>
        ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task<object?> ExecuteScalarAsync(
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<QueryResult> results = await ExecuteAllAsync(cancellationToken)
            .ConfigureAwait(false);
        if (results.Count == 0)
        {
            return null;
        }

        QueryResult first = results[0];
        if (first.Columns.Count == 0 || first.Rows.Count == 0)
        {
            return null;
        }

        return first.Rows[0][0] ?? DBNull.Value;
    }

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        ExecuteDbDataReaderAsync(behavior, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc />
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<QueryResult> results = await ExecuteAllAsync(cancellationToken)
            .ConfigureAwait(false);
        return new DotRocksDataReader(results, _connection, behavior);
    }

    private async Task<IReadOnlyList<QueryResult>> ExecuteAllAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connection is null)
        {
            throw new InvalidOperationException("Batch requires a DotRocksConnection.");
        }

        _connection.ValidateCommandTransaction((DotRocksTransaction?)_transaction);

        using var scope = new ActiveOperationScope(
            _activeOperationGate,
            _timeout,
            "Concurrent execution of the same DotRocksBatch is not supported.",
            cancellationToken
        );
        try
        {
            var results = new List<QueryResult>(_batchCommands.Count);
            foreach (DbBatchCommand command in _batchCommands)
            {
                var batchCommand = (DotRocksBatchCommand)command;
                if (batchCommand.CommandType != CommandType.Text)
                {
                    throw new NotSupportedException("DotRocks supports only text commands.");
                }

                string commandText = CommandTextParameterBinder.Bind(
                    batchCommand.CommandText,
                    batchCommand.DotRocksParameters
                );
                QueryResult result = await _connection
                    .ExecuteQueryAsync(commandText, scope.Token)
                    .ConfigureAwait(false);
                batchCommand.SetRecordsAffected(result.RecordsAffected);
                results.Add(result);
            }

            return results;
        }
        catch (OperationCanceledException ex) when (scope.IsCanceledByCancelMethod)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            throw new OperationCanceledException(
                "The DotRocks batch was canceled.",
                ex,
                scope.OperationToken
            );
        }
    }
}
