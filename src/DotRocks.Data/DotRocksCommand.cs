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
    private readonly Lock _activeCommandGate = new();
    private readonly DotRocksParameterCollection _parameters = new();
    private CancellationTokenSource? _activeCommandCancellation;
    private DotRocksConnection? _connection;
    private DbTransaction? _transaction;
    private string _commandText = string.Empty;
    private int _commandTimeout = 30;
    private PreparedCommandText? _preparedCommand;
    private DotRocksParameterMode _parameterMode = DotRocksParameterMode.Auto;

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

    /// <summary>
    /// Gets or sets how parameters are bound and the command is executed. Defaults to
    /// <see cref="DotRocksParameterMode.Auto"/>. <see cref="DotRocksParameterMode.ServerPrepared"/>
    /// uses the StarRocks server-side prepared-statement protocol.
    /// </summary>
    public DotRocksParameterMode ParameterMode
    {
        get => _parameterMode;
        set => _parameterMode = value;
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
        if (_parameterMode == DotRocksParameterMode.ServerPrepared)
        {
            // Server-side preparation happens at execution against the live connection.
            return;
        }

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
        cancellationToken.ThrowIfCancellationRequested();
        if (_connection is null)
        {
            throw new InvalidOperationException("Command requires a DotRocksConnection.");
        }

        _connection.ValidateCommandTransaction((DotRocksTransaction?)_transaction);
        bool serverPrepared = _parameterMode == DotRocksParameterMode.ServerPrepared;

        // Server-prepared statements use positional `?` placeholders, so the raw command text is
        // sent to COM_STMT_PREPARE and values are bound positionally. The text path tokenizes
        // named `@` parameters and inlines safely-formatted literals.
        string commandText = serverPrepared ? CommandText : BindCommandText();
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
        DotRocksTelemetryTags.TagCommandStart(activity, commandText);
        long startTimestamp = Stopwatch.GetTimestamp();
        bool succeeded = false;
        string? errorType = null;
        string? statusCode = null;
        try
        {
            DotRocksDataReader reader;
            bool hasResultSet;
            if (serverPrepared)
            {
                QueryResult preparedResult = await _connection
                    .ExecutePreparedQueryAsync(
                        commandText,
                        ExtractServerPreparedValues(),
                        linkedCancellation.Token
                    )
                    .ConfigureAwait(false);
                reader = new DotRocksDataReader(preparedResult, _connection, behavior);
                hasResultSet = preparedResult.HasResultSet;
            }
            else
            {
                StreamingQueryResult result = await _connection
                    .ExecuteStreamingQueryAsync(commandText, linkedCancellation.Token)
                    .ConfigureAwait(false);
                reader = new DotRocksDataReader(result, _connection, behavior);
                hasResultSet = result.HasResultSet;
            }

            if (hasResultSet)
            {
                _connection.SetActiveReader(reader);
            }

            succeeded = true;
            return reader;
        }
        catch (OperationCanceledException ex)
            when (IsCommandTimeout(timeoutCancellation, commandCancellation, cancellationToken))
        {
            errorType = DotRocksTelemetryTags.ErrorTimeout;
            await _connection.CloseAsync().ConfigureAwait(false);
            throw CreateCommandTimeoutException(ex);
        }
        catch (DotRocksException ex)
            when (IsCommandTimeout(timeoutCancellation, commandCancellation, cancellationToken))
        {
            errorType = DotRocksTelemetryTags.ErrorTimeout;
            await _connection.CloseAsync().ConfigureAwait(false);
            throw CreateCommandTimeoutException(ex);
        }
        catch (OperationCanceledException ex)
            when (commandCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
            )
        {
            errorType = DotRocksTelemetryTags.ErrorCanceled;
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
            errorType = DotRocksTelemetryTags.ErrorCanceled;
            await _connection.CloseAsync().ConfigureAwait(false);
            throw new OperationCanceledException(
                "The DotRocks command was canceled.",
                ex,
                commandCancellation.Token
            );
        }
        catch (OperationCanceledException)
        {
            errorType = DotRocksTelemetryTags.ErrorCanceled;
            await _connection.CloseAsync().ConfigureAwait(false);
            throw;
        }
        catch (DotRocksException ex)
        {
            (errorType, statusCode) = DotRocksTelemetryTags.Classify(ex);
            throw;
        }
        catch (Exception ex)
        {
            (errorType, statusCode) = DotRocksTelemetryTags.Classify(ex);
            throw;
        }
        finally
        {
            if (succeeded)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                DotRocksTelemetryTags.TagError(activity, errorType ?? "OTHER", statusCode);
            }

            // Bounded metric labels only: outcome in {success, error, canceled, timeout} and a
            // low-cardinality operation name. Never SQL text, parameters, or identifiers.
            var tags = new TagList
            {
                { "outcome", DotRocksTelemetryTags.OutcomeFor(succeeded, errorType) },
                { "operation", DotRocksTelemetryTags.ClassifyOperation(commandText) },
            };
            DotRocksTelemetry.CommandDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                tags
            );
            DotRocksTelemetry.CommandsExecuted.Add(1, tags);
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
        // A prepared command whose text still matches binds from the cached tokenized template,
        // avoiding a re-scan of the command text on every execution.
        if (
            _preparedCommand is not null
            && string.Equals(_preparedCommand.CommandText, CommandText, StringComparison.Ordinal)
        )
        {
            return CommandTextParameterBinder.BindPrepared(_preparedCommand, _parameters);
        }

        _preparedCommand = null;
        return CommandTextParameterBinder.Bind(CommandText, _parameters);
    }

    private void EnsureTextCommand()
    {
        if (CommandType != CommandType.Text)
        {
            throw new NotSupportedException("DotRocks supports only text commands.");
        }
    }

    // Positional parameter values for a server-prepared statement, in collection order.
    private object?[] ExtractServerPreparedValues()
    {
        object?[] values = new object?[_parameters.Count];
        for (int i = 0; i < _parameters.Count; i++)
        {
            values[i] = ((DotRocksParameter)_parameters[i]).Value;
        }

        return values;
    }
}
