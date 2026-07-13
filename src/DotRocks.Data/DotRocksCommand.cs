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
    private readonly ActiveOperationGate _activeOperationGate = new();
    private readonly DotRocksParameterCollection _parameters = new();
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
        if (_activeOperationGate.TryCancelActiveOperation())
        {
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
        using DbDataReader reader = ExecuteDbDataReader(
            CommandBehavior.SingleResult | CommandBehavior.SingleRow
        );
        if (!reader.Read())
        {
            return null;
        }

        object? value = reader.FieldCount == 0 ? null : reader.GetValue(0);
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
        DbDataReader reader = await ExecuteDbDataReaderAsync(
                CommandBehavior.Default,
                cancellationToken
            )
            .ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return reader.RecordsAffected;
        }
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
            return value == DBNull.Value ? null : value;
        }
    }

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter() => new DotRocksParameter();

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("Command requires a DotRocksConnection.");
        }

        _connection.ValidateCommandTransaction((DotRocksTransaction?)_transaction);
        bool serverPrepared = _parameterMode == DotRocksParameterMode.ServerPrepared;
        string commandText = serverPrepared ? CommandText : BindCommandText();
        using var scope = new ActiveOperationScope(
            _activeOperationGate,
            CommandTimeout,
            "Concurrent execution of the same DotRocksCommand is not supported.",
            CancellationToken.None
        );
        using CancellationTokenRegistration cancellationRegistration = scope.Token.Register(
            static state => ((DotRocksConnection)state!).Abort(),
            _connection
        );
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
            StreamingQueryResult result = serverPrepared
                ? _connection.ExecutePreparedStreamingQuery(
                    commandText,
                    ExtractServerPreparedValues()
                )
                : _connection.ExecuteStreamingQuery(commandText);
            var reader = new DotRocksDataReader(result, _connection, behavior);
            if (result.HasResultSet)
            {
                _connection.SetActiveReader(reader);
            }

            succeeded = true;
            return reader;
        }
        catch (Exception ex) when (scope.IsTimeout)
        {
            errorType = DotRocksTelemetryTags.ErrorTimeout;
            _connection.Close();
            throw CreateCommandTimeoutException(ex);
        }
        catch (Exception ex) when (scope.IsCanceledByCancelMethod)
        {
            errorType = DotRocksTelemetryTags.ErrorCanceled;
            _connection.Close();
            throw new OperationCanceledException(
                "The DotRocks command was canceled.",
                ex,
                scope.OperationToken
            );
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
            RecordCommandCompletion(
                activity,
                startTimestamp,
                commandText,
                succeeded,
                errorType,
                statusCode
            );
        }
    }

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
        using var scope = new ActiveOperationScope(
            _activeOperationGate,
            CommandTimeout,
            "Concurrent execution of the same DotRocksCommand is not supported.",
            cancellationToken
        );

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
            StreamingQueryResult result;
            if (serverPrepared)
            {
                result = await _connection
                    .ExecutePreparedStreamingQueryAsync(
                        commandText,
                        ExtractServerPreparedValues(),
                        scope.Token
                    )
                    .ConfigureAwait(false);
            }
            else
            {
                result = await _connection
                    .ExecuteStreamingQueryAsync(commandText, scope.Token)
                    .ConfigureAwait(false);
            }

            var reader = new DotRocksDataReader(result, _connection, behavior);

            if (result.HasResultSet)
            {
                _connection.SetActiveReader(reader);
            }

            succeeded = true;
            return reader;
        }
        catch (OperationCanceledException ex) when (scope.IsTimeout)
        {
            errorType = DotRocksTelemetryTags.ErrorTimeout;
            await _connection.CloseAsync().ConfigureAwait(false);
            throw CreateCommandTimeoutException(ex);
        }
        catch (DotRocksException ex) when (scope.IsTimeout)
        {
            errorType = DotRocksTelemetryTags.ErrorTimeout;
            await _connection.CloseAsync().ConfigureAwait(false);
            throw CreateCommandTimeoutException(ex);
        }
        catch (OperationCanceledException ex) when (scope.IsCanceledByCancelMethod)
        {
            errorType = DotRocksTelemetryTags.ErrorCanceled;
            await _connection.CloseAsync().ConfigureAwait(false);
            throw new OperationCanceledException(
                "The DotRocks command was canceled.",
                ex,
                scope.OperationToken
            );
        }
        catch (DotRocksException ex) when (scope.IsCanceledByCancelMethod)
        {
            errorType = DotRocksTelemetryTags.ErrorCanceled;
            await _connection.CloseAsync().ConfigureAwait(false);
            throw new OperationCanceledException(
                "The DotRocks command was canceled.",
                ex,
                scope.OperationToken
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
            RecordCommandCompletion(
                activity,
                startTimestamp,
                commandText,
                succeeded,
                errorType,
                statusCode
            );
        }
    }

    private static void RecordCommandCompletion(
        Activity? activity,
        long startTimestamp,
        string commandText,
        bool succeeded,
        string? errorType,
        string? statusCode
    )
    {
        if (succeeded)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            DotRocksTelemetryTags.TagError(activity, errorType ?? "OTHER", statusCode);
        }

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
    }

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
