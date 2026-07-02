using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data.Diagnostics;
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
    private DotRocksDataReader? _activeReader;

    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The active transaction is owned by the caller; the connection only tracks and completes it."
    )]
    private DotRocksTransaction? _activeTransaction;
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

    // Used by DotRocksDataSource so credentialed options flow without a round trip through the
    // public ConnectionString property, which is redacted and would drop the password.
    internal DotRocksConnection(DotRocksConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Closes and removes all idle physical connections from all DotRocks connection pools.
    /// </summary>
    public static void ClearAllPools() => DotRocksConnectionPool.ClearAll();

    /// <summary>
    /// Closes and removes idle physical connections from the pool for the given connection's
    /// configuration. Connections currently in use are unaffected.
    /// </summary>
    /// <param name="connection">A connection identifying the pool to clear.</param>
    public static void ClearPool(DotRocksConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        DotRocksConnectionPool.Clear(connection._options);
    }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString
    {
        // Never return the password from the getter (ADO PersistSecurityInfo=false convention), so
        // logging or echoing ConnectionString cannot leak the secret.
        get => _options.ToRedactedConnectionString();
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
    protected override DbProviderFactory DbProviderFactory => DotRocksFactory.Instance;

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
        if (_activeReader is not null)
        {
            _activeReader.MarkCompletedBecauseConnectionClosed();
            _activeReader = null;
            reusable = false;
        }

        if (_activeTransaction is not null)
        {
            _activeTransaction.MarkCompletedBecauseConnectionClosed();
            _activeTransaction = null;
            reusable = false;
        }

        DotRocksConnectionPoolLease? lease = _lease;
        _lease = null;
        _serverVersion = string.Empty;
        _state = ConnectionState.Closed;
        lease?.Return(reusable);
    }

    /// <inheritdoc />
    public override void Open() =>
        OpenAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

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

        using Activity? activity = DotRocksTelemetry.ActivitySource.StartActivity(
            "dotrocks.connection.open",
            ActivityKind.Client
        );
        DotRocksTelemetryTags.TagConnectionOpen(activity, _options);
        long startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            _lease = await OpenLeaseWithRetryAsync(linked.Token).ConfigureAwait(false);
            _serverVersion = _lease.PhysicalConnection.ServerVersion;
            _state = ConnectionState.Open;
            DotRocksTelemetry.ConnectionsOpened.Add(1);
            RecordConnectionOpenDuration(startTimestamp, "success");
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            // Never attach the raw exception message to the span; classify it instead.
            bool timedOut =
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            (string errorType, string? statusCode) = DotRocksTelemetryTags.Classify(ex);
            DotRocksTelemetryTags.TagError(
                activity,
                timedOut ? DotRocksTelemetryTags.ErrorTimeout : errorType,
                statusCode
            );
            RecordConnectionOpenDuration(startTimestamp, timedOut ? "timeout" : "error");
            CloseCore(reusable: false);
            throw;
        }
    }

    private static void RecordConnectionOpenDuration(long startTimestamp, string outcome) =>
        DotRocksTelemetry.ConnectionOpenDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new TagList { { "outcome", outcome } }
        );

    private async ValueTask<DotRocksConnectionPoolLease> OpenLeaseWithRetryAsync(
        CancellationToken cancellationToken
    )
    {
        int maxAttempts = _options.MaxConnectionRetries + 1;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await OpenLeaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DotRocksException ex)
                when (ex.IsTransient
                    && attempt < maxAttempts
                    && !cancellationToken.IsCancellationRequested
                )
            {
                // Opening a connection is idempotent, so a transient failure is safe to retry.
                if (_options.ConnectionRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.ConnectionRetryDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        BeginDbTransactionAsync(isolationLevel, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc />
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken
    )
    {
        ValidateTransactionIsolationLevel(isolationLevel);
        if (_state != ConnectionState.Open || _lease is null)
        {
            throw new InvalidOperationException("The connection is not open.");
        }

        if (_activeTransaction is not null)
        {
            throw new InvalidOperationException(
                "The DotRocks connection already has an active transaction."
            );
        }

        var transaction = new DotRocksTransaction(this, isolationLevel);
        await ExecuteTransactionCommandAsync("START TRANSACTION", transaction, cancellationToken)
            .ConfigureAwait(false);
        _activeTransaction = transaction;
        return transaction;
    }

    /// <summary>
    /// Creates a <see cref="DotRocksCommand"/> associated with this connection.
    /// </summary>
    public new DotRocksCommand CreateCommand() => CreateDotRocksCommand();

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => CreateDotRocksCommand();

    private DotRocksCommand CreateDotRocksCommand() => new(this);

    /// <summary>Returns the list of supported schema metadata collections.</summary>
    public override DataTable GetSchema() =>
        Metadata.DotRocksSchema.GetSchema(this, collectionName: null, restrictions: null);

    /// <summary>Returns the named schema metadata collection from StarRocks INFORMATION_SCHEMA.</summary>
    /// <param name="collectionName">The metadata collection name (for example "Tables" or "Columns").</param>
    public override DataTable GetSchema(string collectionName) =>
        Metadata.DotRocksSchema.GetSchema(this, collectionName, restrictions: null);

    /// <summary>Returns the named schema metadata collection filtered by the given restrictions.</summary>
    /// <param name="collectionName">The metadata collection name.</param>
    /// <param name="restrictionValues">Restriction values applied in the collection's documented order.</param>
    public override DataTable GetSchema(string collectionName, string?[] restrictionValues) =>
        Metadata.DotRocksSchema.GetSchema(this, collectionName, restrictionValues);

    /// <inheritdoc />
    public override bool CanCreateBatch => true;

    /// <inheritdoc />
    protected override DbBatch CreateDbBatch() => new DotRocksBatch(this);

    internal ValueTask<QueryResult> ExecuteQueryAsync(
        string commandText,
        CancellationToken cancellationToken
    ) =>
        ExecuteOnPhysicalAsync(physical =>
            physical.ExecuteQueryAsync(commandText, cancellationToken)
        );

    internal ValueTask<QueryResult> ExecutePreparedQueryAsync(
        string commandText,
        IReadOnlyList<object?> parameterValues,
        CancellationToken cancellationToken
    ) =>
        ExecuteOnPhysicalAsync(async physical =>
        {
            // Reuse a cached server-prepared statement for this connection when possible; the
            // statement stays open and session-scoped, so it is not closed after each execution.
            StatementPrepareResult prepared = await physical
                .PrepareCachedAsync(commandText, cancellationToken)
                .ConfigureAwait(false);

            if (prepared.ParameterCount != parameterValues.Count)
            {
                throw new DotRocksException(
                    $"The prepared statement expects {prepared.ParameterCount} parameter(s) but {parameterValues.Count} were supplied."
                );
            }

            return await physical
                .ExecutePreparedAsync(prepared.StatementId, parameterValues, cancellationToken)
                .ConfigureAwait(false);
        });

    internal ValueTask<StreamingQueryResult> ExecuteStreamingQueryAsync(
        string commandText,
        CancellationToken cancellationToken
    ) =>
        ExecuteOnPhysicalAsync(physical =>
            physical.ExecuteQueryStreamingAsync(commandText, cancellationToken)
        );

    // Shared guard/failure skeleton for executing one operation against the leased physical
    // connection: the connection must be open with no active reader, and failures close the
    // logical connection only when the protocol state is broken.
    private async ValueTask<T> ExecuteOnPhysicalAsync<T>(
        Func<DotRocksPhysicalConnection, ValueTask<T>> operation
    )
    {
        DotRocksConnectionPoolLease? lease = _lease;
        if (_state != ConnectionState.Open || lease is null)
        {
            throw new InvalidOperationException("The connection is not open.");
        }

        ValidateNoActiveReader();

        try
        {
            return await operation(lease.PhysicalConnection).ConfigureAwait(false);
        }
        catch
        {
            // Close only when the protocol state is broken (I/O failure, malformed packet,
            // cancellation mid-command). A plain server error leaves the connection at a clean
            // packet boundary and must not close it, even when pool policy (session dirtiness,
            // lifetime) would decline to reuse the physical connection later.
            if (lease.PhysicalConnection.IsBroken)
            {
                CloseCore(reusable: false);
            }

            throw;
        }
    }

    internal void SetActiveReader(DotRocksDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ValidateNoActiveReader();
        _activeReader = reader;
    }

    internal void CompleteActiveReader(DotRocksDataReader reader, bool reusable)
    {
        if (!ReferenceEquals(_activeReader, reader))
        {
            return;
        }

        _activeReader = null;
        if (!reusable)
        {
            Abort();
        }
    }

    internal async ValueTask ExecuteTransactionCommandAsync(
        string commandText,
        DotRocksTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.DotRocksConnection, this))
        {
            throw new InvalidOperationException(
                "The transaction does not belong to this DotRocks connection."
            );
        }

        QueryResult result = await ExecuteQueryAsync(commandText, cancellationToken)
            .ConfigureAwait(false);
        if (result.HasResultSet)
        {
            throw new DotRocksException(
                "StarRocks returned a result set for a transaction control command."
            );
        }
    }

    internal void ClearActiveTransaction(DotRocksTransaction transaction)
    {
        if (ReferenceEquals(_activeTransaction, transaction))
        {
            _activeTransaction = null;
        }
    }

    internal void AbortTransaction(DotRocksTransaction transaction)
    {
        if (!ReferenceEquals(_activeTransaction, transaction))
        {
            return;
        }

        _activeTransaction = null;
        Abort();
    }

    internal void RollbackTransactionForDispose(DotRocksTransaction transaction) =>
        RollbackTransactionForDisposeAsync(transaction)
            .AsTask()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    internal async ValueTask RollbackTransactionForDisposeAsync(DotRocksTransaction transaction)
    {
        if (!ReferenceEquals(_activeTransaction, transaction))
        {
            return;
        }

        if (_state != ConnectionState.Open || _lease is null)
        {
            // The physical connection is already gone; just detach the transaction.
            _activeTransaction = null;
            return;
        }

        try
        {
            await ExecuteTransactionCommandAsync(
                    "ROLLBACK WORK",
                    transaction,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            _activeTransaction = null;
        }
        catch (DotRocksException)
        {
            // The rollback itself failed (e.g. a broken connection). Discard the physical
            // connection rather than returning a connection with an undefined transaction state.
            AbortTransaction(transaction);
        }
        catch (ObjectDisposedException)
        {
            AbortTransaction(transaction);
        }
    }

    internal void ValidateCommandTransaction(DotRocksTransaction? transaction)
    {
        if (transaction is not null)
        {
            transaction.EnsureActive();
            if (!ReferenceEquals(transaction.DotRocksConnection, this))
            {
                throw new InvalidOperationException(
                    "The command transaction does not belong to its DotRocks connection."
                );
            }
        }

        if (_activeTransaction is null)
        {
            return;
        }

        if (!ReferenceEquals(_activeTransaction, transaction))
        {
            throw new InvalidOperationException(
                "Commands executed while a transaction is active must reference that transaction."
            );
        }
    }

    internal void ValidateNoActiveReader()
    {
        if (_activeReader is not null)
        {
            throw new InvalidOperationException(
                "The DotRocks connection already has an active reader."
            );
        }
    }

    private async ValueTask<DotRocksConnectionPoolLease> OpenLeaseAsync(
        CancellationToken cancellationToken
    )
    {
        if (_options.Pooling)
        {
            return await DotRocksConnectionPool
                .LeaseFromPoolAsync(_options, cancellationToken)
                .ConfigureAwait(false);
        }

        DotRocksPhysicalConnection physicalConnection = await DotRocksPhysicalConnection
            .OpenAsync(_options, cancellationToken)
            .ConfigureAwait(false);
        return DotRocksConnectionPoolLease.Unpooled(physicalConnection);
    }

    private static void ValidateTransactionIsolationLevel(IsolationLevel isolationLevel)
    {
        if (isolationLevel is IsolationLevel.Unspecified or IsolationLevel.ReadCommitted)
        {
            return;
        }

        throw new NotSupportedException(
            $"DotRocks does not support transaction isolation level '{isolationLevel}'."
        );
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
