using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data.Loading;
using DotRocks.Data.Pooling;
using DotRocks.Data.Protocol.Results;

namespace DotRocks.Data;

/// <summary>
/// Represents a connection to a StarRocks FE query endpoint.
/// </summary>
public sealed class DotRocksConnection : DbConnection
{
    private DotRocksConnectionOptions _options;
    private DotRocksConnectionPoolLease? _lease;
    private string _serverVersion = string.Empty;
    private ConnectionState _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksConnection"/> class.
    /// </summary>
    public DotRocksConnection()
    {
        _options = DotRocksConnectionOptions.Default;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksConnection"/> class.
    /// </summary>
    /// <param name="connectionString">The connection string to use.</param>
    public DotRocksConnection(string connectionString)
        : this()
    {
        ConnectionString = connectionString;
    }

    /// <summary>
    /// Closes and removes all idle physical connections from all DotRocks connection pools.
    /// </summary>
    public static void ClearAllPools() => DotRocksConnectionPool.ClearAll();

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString
    {
        get => _options.ConnectionString;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException(
                    "The connection string cannot be changed while the connection is open."
                );
            }

            _options = DotRocksConnectionOptions.Parse(value);
        }
    }

    /// <inheritdoc />
    public override string Database => _options.Database;

    /// <inheritdoc />
    public override string DataSource => _options.Server;

    /// <inheritdoc />
    public override string ServerVersion => _serverVersion;

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException(
            "Changing the database on an open DotRocks connection is not supported yet."
        );

    /// <inheritdoc />
    public override void Close()
    {
        CloseCore(reusable: true);
    }

    /// <inheritdoc />
    public override Task CloseAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    internal void Abort()
    {
        _lease?.PhysicalConnection.MarkBroken();
        CloseCore(reusable: false);
    }

    private void CloseCore(bool reusable)
    {
        DotRocksConnectionPoolLease? lease = _lease;
        _lease = null;
        _serverVersion = string.Empty;
        _state = ConnectionState.Closed;
        lease?.Return(reusable);
    }

    /// <inheritdoc />
    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The connection is already open.");
        }

        _state = ConnectionState.Connecting;
        using var timeout = new CancellationTokenSource(_options.ConnectionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token
        );

        try
        {
            _lease = await OpenLeaseAsync(linked.Token).ConfigureAwait(false);
            _serverVersion = _lease.PhysicalConnection.ServerVersion;
            _state = ConnectionState.Open;
        }
        catch
        {
            CloseCore(reusable: false);
            throw;
        }
    }

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("Transactions are not implemented yet.");

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new DotRocksCommand(this);

    internal async ValueTask<QueryResult> ExecuteQueryAsync(
        string commandText,
        CancellationToken cancellationToken
    )
    {
        DotRocksConnectionPoolLease? lease = _lease;
        if (_state != ConnectionState.Open || lease is null)
        {
            throw new InvalidOperationException("The connection is not open.");
        }

        try
        {
            return await lease
                .PhysicalConnection.ExecuteQueryAsync(commandText, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (!lease.PhysicalConnection.IsReusable)
            {
                CloseCore(reusable: false);
            }

            throw;
        }
    }

    private async ValueTask<DotRocksConnectionPoolLease> OpenLeaseAsync(
        CancellationToken cancellationToken
    )
    {
        if (_options.Pooling)
        {
            return await DotRocksConnectionPool
                .GetPool(_options)
                .LeaseAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        DotRocksPhysicalConnection physicalConnection = await DotRocksPhysicalConnection
            .OpenAsync(_options, cancellationToken)
            .ConfigureAwait(false);
        return DotRocksConnectionPoolLease.Unpooled(physicalConnection);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }
}
