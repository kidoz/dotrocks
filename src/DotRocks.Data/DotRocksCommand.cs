using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data.Diagnostics;
using DotRocks.Data.Protocol.Commands;
using DotRocks.Data.Protocol.Results;

namespace DotRocks.Data;

/// <summary>
/// Represents a SQL command executed against StarRocks.
/// </summary>
public sealed class DotRocksCommand : DbCommand
{
    private readonly object _activeCommandGate = new();
    private readonly DotRocksParameterCollection _parameters = new();
    private CancellationTokenSource? _activeCommandCancellation;
    private DotRocksConnection? _connection;
    private DbTransaction? _transaction;
    private string _commandText = string.Empty;
    private int _commandTimeout = 30;
    private PreparedCommandText? _preparedCommand;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksCommand"/> class.
    /// </summary>
    public DotRocksCommand() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksCommand"/> class.
    /// </summary>
    /// <param name="commandText">The SQL command text.</param>
    /// <param name="connection">The open DotRocks connection.</param>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "This constructor stores command text; execution and parameterization are validated separately."
    )]
    public DotRocksCommand(string commandText, DotRocksConnection connection)
    {
        CommandText = commandText;
        Connection = connection;
    }

    internal DotRocksCommand(DotRocksConnection connection)
    {
        Connection = connection;
    }

    /// <inheritdoc />
    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            _commandText = value ?? string.Empty;
            _preparedCommand = null;
        }
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
                throw new NotSupportedException("DotRocks supports only text commands.");
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
        set => _connection = (DotRocksConnection?)value;
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

            if (value is not DotRocksTransaction transaction)
            {
                throw new InvalidOperationException(
                    "DotRocksCommand accepts only DotRocksTransaction instances."
                );
            }

            transaction.EnsureActive();
            if (
                _connection is not null
                && !ReferenceEquals(_connection, transaction.DotRocksConnection)
            )
            {
                throw new InvalidOperationException(
                    "The command transaction does not belong to the command connection."
                );
            }

            _connection ??= transaction.DotRocksConnection;
            _transaction = transaction;
        }
    }

    /// <inheritdoc />
    public override void Cancel()
    {
        CancellationTokenSource? activeCommandCancellation;
        lock (_activeCommandGate)
        {
            activeCommandCancellation = _activeCommandCancellation;
        }

        if (activeCommandCancellation is not null)
        {
            activeCommandCancellation.Cancel();
            _connection?.Abort();
        }
    }

    /// <inheritdoc />
    public override int ExecuteNonQuery()
    {
        using DbDataReader reader = ExecuteDbDataReader(CommandBehavior.Default);
        return reader.RecordsAffected;
    }

    /// <inheritdoc />
    public override object? ExecuteScalar()
    {
        using DbDataReader reader = ExecuteDbDataReader(CommandBehavior.SingleResult);
        if (!reader.Read())
        {
            return null;
        }

        object? value = reader.FieldCount == 0 ? null : reader.GetValue(0);
        while (reader.Read()) { }

        return value == DBNull.Value ? null : value;
    }

    /// <inheritdoc />
    public override void Prepare()
    {
        EnsureTextCommand();
        _preparedCommand = CommandTextParameterBinder.Prepare(CommandText, _parameters);
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
        using DbDataReader reader = await ExecuteDbDataReaderAsync(
                CommandBehavior.Default,
                cancellationToken
            )
            .ConfigureAwait(false);
        return reader.RecordsAffected;
    }

    /// <inheritdoc />
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        using DbDataReader reader = await ExecuteDbDataReaderAsync(
                CommandBehavior.SingleResult,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        object? value = reader.FieldCount == 0 ? null : reader.GetValue(0);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { }

        return value == DBNull.Value ? null : value;
    }

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter() => new DotRocksParameter();

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connection is null)
        {
            throw new InvalidOperationException("Command requires a DotRocksConnection.");
        }

        _connection.ValidateCommandTransaction((DotRocksTransaction?)_transaction);
        string commandText = BindCommandText();
        using var commandCancellation = new CancellationTokenSource();
        using CancellationTokenSource? timeoutCancellation = CreateTimeoutCancellation();
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(
            commandCancellation,
            timeoutCancellation,
            cancellationToken
        );

        SetActiveCommandCancellation(commandCancellation);
        using Activity? activity = DotRocksTelemetry.ActivitySource.StartActivity(
            "dotrocks.command.execute",
            ActivityKind.Client
        );
        long startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            StreamingQueryResult result = await _connection
                .ExecuteStreamingQueryAsync(commandText, linkedCancellation.Token)
                .ConfigureAwait(false);
            var reader = new DotRocksDataReader(result, _connection, behavior);
            if (result.HasResultSet)
            {
                _connection.SetActiveReader(reader);
            }

            return reader;
        }
        catch (OperationCanceledException ex)
            when (IsCommandTimeout(timeoutCancellation, commandCancellation, cancellationToken))
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            throw CreateCommandTimeoutException(ex);
        }
        catch (DotRocksException ex)
            when (IsCommandTimeout(timeoutCancellation, commandCancellation, cancellationToken))
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            throw CreateCommandTimeoutException(ex);
        }
        catch (OperationCanceledException ex)
            when (commandCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
            )
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            throw new OperationCanceledException(
                "The DotRocks command was canceled.",
                ex,
                commandCancellation.Token
            );
        }
        catch (DotRocksException ex)
            when (commandCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
            )
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            throw new OperationCanceledException(
                "The DotRocks command was canceled.",
                ex,
                commandCancellation.Token
            );
        }
        catch (OperationCanceledException)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            DotRocksTelemetry.CommandDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
            );
            DotRocksTelemetry.CommandsExecuted.Add(1);
            ClearActiveCommandCancellation(commandCancellation);
        }
    }

    private CancellationTokenSource? CreateTimeoutCancellation()
    {
        if (CommandTimeout == 0)
        {
            return null;
        }

        return new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeout));
    }

    private static CancellationTokenSource CreateLinkedCancellation(
        CancellationTokenSource commandCancellation,
        CancellationTokenSource? timeoutCancellation,
        CancellationToken cancellationToken
    )
    {
        if (timeoutCancellation is null)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                commandCancellation.Token
            );
        }

        return CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            commandCancellation.Token,
            timeoutCancellation.Token
        );
    }

    private void SetActiveCommandCancellation(CancellationTokenSource commandCancellation)
    {
        lock (_activeCommandGate)
        {
            if (_activeCommandCancellation is not null)
            {
                throw new InvalidOperationException(
                    "Concurrent execution of the same DotRocksCommand is not supported."
                );
            }

            _activeCommandCancellation = commandCancellation;
        }
    }

    private void ClearActiveCommandCancellation(CancellationTokenSource commandCancellation)
    {
        lock (_activeCommandGate)
        {
            if (ReferenceEquals(_activeCommandCancellation, commandCancellation))
            {
                _activeCommandCancellation = null;
            }
        }
    }

    private static bool IsCommandTimeout(
        CancellationTokenSource? timeoutCancellation,
        CancellationTokenSource commandCancellation,
        CancellationToken userCancellationToken
    ) =>
        timeoutCancellation?.IsCancellationRequested == true
        && !userCancellationToken.IsCancellationRequested
        && !commandCancellation.IsCancellationRequested;

    private static DotRocksException CreateCommandTimeoutException(Exception innerException) =>
        new(
            "The DotRocks command timed out.",
            serverErrorCode: null,
            sqlState: null,
            isTransient: true,
            connectionId: null,
            innerException
        );

    private string BindCommandText()
    {
        if (_preparedCommand is not null)
        {
            if (!string.Equals(_preparedCommand.CommandText, CommandText, StringComparison.Ordinal))
            {
                _preparedCommand = null;
            }
            else
            {
                CommandTextParameterBinder.Prepare(CommandText, _parameters);
            }
        }

        return CommandTextParameterBinder.Bind(CommandText, _parameters);
    }

    private void EnsureTextCommand()
    {
        if (CommandType != CommandType.Text)
        {
            throw new NotSupportedException("DotRocks supports only text commands.");
        }
    }
}
