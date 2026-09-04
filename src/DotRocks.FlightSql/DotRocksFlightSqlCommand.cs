using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data;
using DotRocks.Data.Protocol.Commands;
using Grpc.Core;

namespace DotRocks.FlightSql;

/// <summary>
/// Represents an asynchronous ADO.NET command executed over Arrow Flight SQL.
/// </summary>
public sealed class DotRocksFlightSqlCommand : DbCommand
{
    private readonly FlightSqlParameterCollection _parameters = new();
    private readonly object _executionSync = new();
    private DbCommand? _activeFallbackCommand;
    private DotRocksFlightSqlDbConnection? _connection;
    private DotRocksFlightSqlDbTransaction? _transaction;
    private CancellationTokenSource? _activeCancellation;
    private string _commandText = string.Empty;
    private int _commandTimeout = 30;
    private bool _executing;

    /// <summary>
    /// Initializes an unassociated Flight SQL command.
    /// </summary>
    public DotRocksFlightSqlCommand() { }

    /// <summary>
    /// Initializes a Flight SQL command associated with a connection.
    /// </summary>
    /// <param name="commandText">The SQL command text.</param>
    /// <param name="connection">The Flight SQL connection.</param>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command text is parameterized before execution."
    )]
    public DotRocksFlightSqlCommand(string commandText, DotRocksFlightSqlDbConnection connection)
    {
        CommandText = commandText;
        Connection = connection;
    }

    internal DotRocksFlightSqlCommand(DotRocksFlightSqlDbConnection connection)
    {
        Connection = connection;
    }

    /// <inheritdoc />
    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override int CommandTimeout
    {
        get => _commandTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _commandTimeout = value;
        }
    }

    /// <inheritdoc />
    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
            {
                throw new NotSupportedException("Flight SQL supports only text commands.");
            }
        }
    }

    /// <inheritdoc />
    public override bool DesignTimeVisible { get; set; }

    /// <inheritdoc />
    public override UpdateRowSource UpdatedRowSource { get; set; }

    /// <inheritdoc />
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (value is not null and not DotRocksFlightSqlDbConnection)
            {
                throw new InvalidOperationException(
                    "DotRocksFlightSqlCommand requires a DotRocksFlightSqlDbConnection."
                );
            }

            _connection = (DotRocksFlightSqlDbConnection?)value;
        }
    }

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            if (value is null)
            {
                _transaction = null;
                return;
            }

            if (value is not DotRocksFlightSqlDbTransaction transaction)
            {
                throw new InvalidOperationException(
                    "DotRocksFlightSqlCommand requires a Flight SQL transaction."
                );
            }

            if (
                _connection is not null
                && !ReferenceEquals(_connection, transaction.OwnerConnection)
            )
            {
                throw new InvalidOperationException(
                    "The command transaction does not belong to the command connection."
                );
            }

            _connection ??= transaction.OwnerConnection;
            _transaction = transaction;
        }
    }

    /// <inheritdoc />
    public override void Cancel()
    {
        CancellationTokenSource? cancellation;
        DbCommand? fallbackCommand;
        lock (_executionSync)
        {
            cancellation = _activeCancellation;
            fallbackCommand = _activeFallbackCommand;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The execution completed between reading the source and cancelling it, which makes
            // cancellation a no-op rather than an error.
        }

        fallbackCommand?.Cancel();
    }

    /// <inheritdoc />
    public override int ExecuteNonQuery() =>
        throw new NotSupportedException(
            "Flight SQL command execution is asynchronous only. Use ExecuteNonQueryAsync."
        );

    /// <inheritdoc />
    public override object? ExecuteScalar() =>
        throw new NotSupportedException(
            "Flight SQL command execution is asynchronous only. Use ExecuteScalarAsync."
        );

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        throw new NotSupportedException(
            "Flight SQL command execution is asynchronous only. Use ExecuteReaderAsync."
        );

    /// <inheritdoc />
    public override void Prepare()
    {
        EnsureTextCommand();
        CommandTextParameterBinder.Prepare(CommandText, _parameters);
    }

    /// <inheritdoc />
    public override Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Prepare();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        DotRocksFlightSqlDbConnection connection = EnsureExecutable();
        string commandText = BindCommandText();
        using ExecutionScope operation = BeginExecution(cancellationToken);

        if (
            _transaction is null
            && connection.FallbackMode.HasFlag(DotRocksFlightSqlFallbackMode.WriteCommands)
        )
        {
            DotRocksConnection fallback = await connection
                .GetFallbackConnectionAsync(operation.Token)
                .ConfigureAwait(false);
            DbCommand fallbackCommand = CreateFallbackCommand(fallback, commandText);
            try
            {
                return await fallbackCommand
                    .ExecuteNonQueryAsync(operation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                ClearFallbackCommand(fallbackCommand);
                await fallbackCommand.DisposeAsync().ConfigureAwait(false);
            }
        }

        long affectedRows = _transaction is null
            ? await connection
                .FlightDataSource.ExecuteUpdateAsync(
                    commandText,
                    operation.CommandTimeout,
                    operation.Token
                )
                .ConfigureAwait(false)
            : await connection
                .FlightDataSource.ExecuteUpdateAsync(
                    commandText,
                    _transaction.FlightTransaction.FlightTransaction,
                    operation.CommandTimeout,
                    operation.Token
                )
                .ConfigureAwait(false);

        return affectedRows > int.MaxValue ? int.MaxValue : checked((int)affectedRows);
    }

    /// <inheritdoc />
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        DbDataReader reader = await ExecuteDbDataReaderAsync(
                CommandBehavior.SingleResult | CommandBehavior.SingleRow,
                cancellationToken
            )
            .ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            object? value = reader.FieldCount == 0 ? null : reader.GetValue(0);
            return value;
        }
    }

    /// <inheritdoc />
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken
    )
    {
        DotRocksFlightSqlDbConnection connection = EnsureExecutable();
        string commandText = BindCommandText();
        ExecutionScope operation = BeginExecution(cancellationToken);

        bool readOnly = SqlStatementClassifier.IsReadOnlyQuery(commandText);
        if (
            _transaction is null
            && connection.FallbackMode.HasFlag(DotRocksFlightSqlFallbackMode.WriteCommands)
            && !readOnly
        )
        {
            return await ExecuteFallbackReaderAsync(connection, commandText, behavior, operation)
                .ConfigureAwait(false);
        }

        // Discovery executes SQL and can fail after a write has committed. Only statements
        // positively classified as reads may be replayed, and only before result fetching starts.
        DotRocksFlightSqlResult result;
        try
        {
            result = _transaction is null
                ? await connection
                    .FlightDataSource.ExecuteQueryAsync(
                        commandText,
                        Apache.Arrow.Flight.Sql.Transaction.NoTransaction,
                        operation.CommandTimeout,
                        operation.Token
                    )
                    .ConfigureAwait(false)
                : await connection
                    .FlightDataSource.ExecuteQueryAsync(
                        commandText,
                        _transaction.FlightTransaction.FlightTransaction,
                        operation.CommandTimeout,
                        operation.Token
                    )
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
            when (_transaction is null
                && readOnly
                && connection.FallbackMode.HasFlag(DotRocksFlightSqlFallbackMode.ReadQueries)
                && IsSafeReadFallbackFailure(ex)
            )
        {
            return await ExecuteFallbackReaderAsync(connection, commandText, behavior, operation)
                .ConfigureAwait(false);
        }
        catch
        {
            operation.Dispose();
            throw;
        }

        try
        {
            return await DotRocksFlightSqlDataReader
                .CreateAsync(
                    result,
                    behavior.HasFlag(CommandBehavior.CloseConnection) ? connection : null,
                    operation,
                    operation.Token
                )
                .ConfigureAwait(false);
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    private async Task<DbDataReader> ExecuteFallbackReaderAsync(
        DotRocksFlightSqlDbConnection connection,
        string commandText,
        CommandBehavior behavior,
        ExecutionScope operation
    )
    {
        try
        {
            DotRocksConnection fallback = await connection
                .GetFallbackConnectionAsync(operation.Token)
                .ConfigureAwait(false);
            DbCommand fallbackCommand = CreateFallbackCommand(fallback, commandText);
            try
            {
                CommandBehavior fallbackBehavior = behavior & ~CommandBehavior.CloseConnection;
                DbDataReader reader = await fallbackCommand
                    .ExecuteReaderAsync(fallbackBehavior, operation.Token)
                    .ConfigureAwait(false);
                return new OwnedFallbackDataReader(
                    reader,
                    fallbackCommand,
                    operation,
                    behavior.HasFlag(CommandBehavior.CloseConnection) ? connection : null,
                    ClearFallbackCommand
                );
            }
            catch
            {
                ClearFallbackCommand(fallbackCommand);
                await fallbackCommand.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter() => new DotRocksParameter();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DbCommand? fallbackCommand;
            lock (_executionSync)
            {
                _activeCancellation?.Cancel();
                _activeCancellation?.Dispose();
                _activeCancellation = null;
                fallbackCommand = _activeFallbackCommand;
                _activeFallbackCommand = null;
            }

            fallbackCommand?.Dispose();
        }

        base.Dispose(disposing);
    }

    private DotRocksFlightSqlDbConnection EnsureExecutable()
    {
        EnsureTextCommand();
        DotRocksFlightSqlDbConnection connection =
            _connection
            ?? throw new InvalidOperationException(
                "Command requires a DotRocksFlightSqlDbConnection."
            );
        connection.EnsureOpen();
        connection.ValidateTransaction(_transaction);
        return connection;
    }

    private void EnsureTextCommand()
    {
        if (CommandType != CommandType.Text)
        {
            throw new NotSupportedException("Flight SQL supports only text commands.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(CommandText);
    }

    private string BindCommandText() => CommandTextParameterBinder.Bind(CommandText, _parameters);

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Named parameters have already been escaped by CommandTextParameterBinder."
    )]
    private DbCommand CreateFallbackCommand(DotRocksConnection connection, string commandText)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = CommandTimeout;
        lock (_executionSync)
        {
            if (_activeFallbackCommand is not null)
            {
                command.Dispose();
                throw new InvalidOperationException(
                    "The Flight SQL command already owns an active fallback command."
                );
            }

            _activeFallbackCommand = command;
        }

        return command;
    }

    private void ClearFallbackCommand(DbCommand command)
    {
        lock (_executionSync)
        {
            if (ReferenceEquals(_activeFallbackCommand, command))
            {
                _activeFallbackCommand = null;
            }
        }
    }

    private ExecutionScope BeginExecution(CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );

        // The source is published together with the executing flag so that Cancel() cannot observe
        // an execution that has started but has no cancellation source yet.
        lock (_executionSync)
        {
            if (_executing)
            {
                source.Dispose();
                throw new InvalidOperationException(
                    "Concurrent execution of the same Flight SQL command is not supported."
                );
            }

            _executing = true;
            _activeCancellation = source;
        }

        TimeSpan? commandTimeout;
        if (CommandTimeout == 0)
        {
            commandTimeout = Timeout.InfiniteTimeSpan;
        }
        else
        {
            commandTimeout = TimeSpan.FromSeconds(CommandTimeout);
            source.CancelAfter(commandTimeout.Value);
        }

        return new ExecutionScope(this, source, commandTimeout);
    }

    private void EndExecution(CancellationTokenSource source)
    {
        lock (_executionSync)
        {
            if (ReferenceEquals(_activeCancellation, source))
            {
                _activeCancellation = null;
            }

            _executing = false;
        }

        source.Dispose();
    }

    private static bool IsSafeReadFallbackFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (
                current is RpcException rpc
                && rpc.StatusCode is StatusCode.Unavailable or StatusCode.Unimplemented
            )
            {
                return true;
            }

            if (current is HttpRequestException)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ExecutionScope : IDisposable
    {
        private readonly DotRocksFlightSqlCommand _command;
        private readonly CancellationTokenSource _source;

        public ExecutionScope(
            DotRocksFlightSqlCommand command,
            CancellationTokenSource source,
            TimeSpan? commandTimeout
        )
        {
            _command = command;
            _source = source;
            CommandTimeout = commandTimeout;
        }

        public CancellationToken Token => _source.Token;

        public TimeSpan? CommandTimeout { get; }

        public void Dispose()
        {
            _command.EndExecution(_source);
        }
    }
}
