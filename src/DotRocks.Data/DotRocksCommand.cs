using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data.Protocol.Results;

namespace DotRocks.Data;

/// <summary>
/// Represents a SQL command executed against StarRocks.
/// </summary>
public sealed class DotRocksCommand : DbCommand
{
    private readonly DotRocksParameterCollection _parameters = new();
    private DotRocksConnection? _connection;
    private DbTransaction? _transaction;
    private string _commandText = string.Empty;

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
        set => _commandText = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override int CommandTimeout { get; set; } = 30;

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
            if (value is not null)
            {
                throw new NotSupportedException("Transactions are not implemented yet.");
            }

            _transaction = null;
        }
    }

    /// <inheritdoc />
    public override void Cancel() { }

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
        if (!reader.Read() || reader.FieldCount == 0)
        {
            return null;
        }

        object value = reader.GetValue(0);
        return value == DBNull.Value ? null : value;
    }

    /// <inheritdoc />
    public override void Prepare() =>
        throw new NotSupportedException("Prepared commands are not implemented yet.");

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
        if (
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.FieldCount == 0
        )
        {
            return null;
        }

        object value = reader.GetValue(0);
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
        if (_connection is null)
        {
            throw new InvalidOperationException("Command requires a DotRocksConnection.");
        }

        if (_parameters.Count > 0)
        {
            throw new NotSupportedException("Command parameters are not implemented yet.");
        }

        QueryResult result = await _connection
            .ExecuteQueryAsync(CommandText, cancellationToken)
            .ConfigureAwait(false);
        return new DotRocksDataReader(result, _connection, behavior);
    }
}
