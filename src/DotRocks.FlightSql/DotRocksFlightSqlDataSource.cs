using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Sql;
using Arrow.Flight.Protocol.Sql;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace DotRocks.FlightSql;

/// <summary>
/// Provides an experimental Arrow Flight SQL transport for StarRocks queries and updates.
/// </summary>
public sealed class DotRocksFlightSqlDataSource : IDisposable
{
    private const string BeginTransactionAction = "BeginTransaction";
    private const string EndTransactionAction = "EndTransaction";
    private static readonly Schema s_emptySchema = new([], null);
    private readonly Dictionary<string, FlightSqlConnection> _connections = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly FlightSqlEndpointPolicy _endpointPolicy;
    private readonly string _password;
    private readonly string _userName;
    private readonly TimeSpan _commandTimeout;
    private readonly Lock _sync = new();
    private int _disposed;

    /// <summary>
    /// Initializes a reusable Flight SQL data source.
    /// </summary>
    /// <param name="options">The validated frontend, credential, timeout, and endpoint policy.</param>
    public DotRocksFlightSqlDataSource(DotRocksFlightSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _endpointPolicy = new FlightSqlEndpointPolicy(options);
        _userName = options.UserName;
        _password = options.Password;
        _commandTimeout = options.CommandTimeout;
    }

    /// <summary>
    /// Starts a SQL query and obtains the schema and endpoint tickets for its streamed result.
    /// </summary>
    /// <param name="sql">A non-empty StarRocks SQL query.</param>
    /// <param name="cancellationToken">A token that cancels query discovery.</param>
    /// <returns>A single-use Arrow record-batch result.</returns>
    public async Task<DotRocksFlightSqlResult> ExecuteQueryAsync(
        string sql,
        CancellationToken cancellationToken = default
    )
    {
        return await ExecuteQueryAsync(
                sql,
                Transaction.NoTransaction,
                commandTimeout: null,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a SQL update and returns the affected-row count reported by StarRocks.
    /// </summary>
    /// <param name="sql">A non-empty DDL or DML statement.</param>
    /// <param name="cancellationToken">A token that cancels the update.</param>
    /// <returns>The affected-row count reported by the server.</returns>
    public Task<long> ExecuteUpdateAsync(
        string sql,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteUpdateAsync(sql, Transaction.NoTransaction, commandTimeout: null, cancellationToken);

    /// <summary>
    /// Begins a Flight SQL transaction.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels transaction creation.</param>
    /// <returns>The new transaction.</returns>
    public async Task<DotRocksFlightSqlTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        FlightSqlConnection connection = GetConnection(_endpointPolicy.PrimaryAddress);
        using CancellationTokenSource timeout = CreateTimeout(_commandTimeout, cancellationToken);
        var request = new ActionBeginTransactionRequest();
        var action = new FlightAction(BeginTransactionAction, Any.Pack(request).ToByteArray());
        using AsyncServerStreamingCall<FlightResult> call = connection.Client.DoAction(
            action,
            connection.CreateHeaders(),
            DateTime.UtcNow.Add(_commandTimeout),
            timeout.Token
        );
        await foreach (
            FlightResult result in call
                .ResponseStream.ReadAllAsync(timeout.Token)
                .ConfigureAwait(false)
        )
        {
            ActionBeginTransactionResult response = Any
                .Parser.ParseFrom(result.Body)
                .Unpack<ActionBeginTransactionResult>();
            if (response.TransactionId.Length == 0)
            {
                throw new InvalidOperationException(
                    "The Flight SQL server returned an empty transaction handle."
                );
            }

            return new DotRocksFlightSqlTransaction(this, new Transaction(response.TransactionId));
        }

        throw new InvalidOperationException(
            "The Flight SQL server did not return a transaction handle."
        );
    }

    internal async Task<DotRocksFlightSqlResult> ExecuteQueryAsync(
        string sql,
        Transaction transaction,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        FlightSqlConnection connection = GetConnection(_endpointPolicy.PrimaryAddress);
        TimeSpan effectiveTimeout = commandTimeout ?? _commandTimeout;
        using CancellationTokenSource timeout = CreateTimeout(effectiveTimeout, cancellationToken);
        var callOptions = new FlightCallOptions { Headers = connection.CreateHeaders() };
        FlightInfo info = await connection
            .SqlClient.ExecuteAsync(
                sql,
                transaction,
                options: callOptions,
                cancellationToken: timeout.Token
            )
            .ConfigureAwait(false);

        return new DotRocksFlightSqlResult(
            info.Schema,
            info.TotalRecords,
            info.TotalBytes,
            info.Ordered,
            token => ReadBatchesAsync(info.Endpoints, effectiveTimeout, token)
        );
    }

    internal async Task<long> ExecuteUpdateAsync(
        string sql,
        Transaction transaction,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        FlightSqlConnection connection = GetConnection(_endpointPolicy.PrimaryAddress);
        TimeSpan effectiveTimeout = commandTimeout ?? _commandTimeout;
        using CancellationTokenSource timeout = CreateTimeout(effectiveTimeout, cancellationToken);
        var update = new CommandStatementUpdate { Query = sql };
        if (transaction.IsValid)
        {
            update.TransactionId = transaction.TransactionId;
        }

        FlightDescriptor descriptor = FlightDescriptor.CreateCommandDescriptor(
            Any.Pack(update).ToByteArray()
        );
        using var call = await connection
            .Client.StartPut(
                descriptor,
                s_emptySchema,
                connection.CreateHeaders(),
                DateTime.UtcNow.Add(effectiveTimeout),
                timeout.Token
            )
            .ConfigureAwait(false);
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        if (!await call.ResponseStream.MoveNext(timeout.Token).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Flight SQL server did not return an update result."
            );
        }

        DoPutUpdateResult result = DoPutUpdateResult.Parser.ParseFrom(
            call.ResponseStream.Current.ApplicationMetadata
        );
        return result.RecordCount;
    }

    internal Task<long> ExecuteUpdateAsync(
        string sql,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken
    ) => ExecuteUpdateAsync(sql, Transaction.NoTransaction, commandTimeout, cancellationToken);

    internal Task CommitTransactionAsync(
        Transaction transaction,
        CancellationToken cancellationToken
    ) => CompleteTransactionAsync(transaction, commit: true, cancellationToken);

    internal Task RollbackTransactionAsync(
        Transaction transaction,
        CancellationToken cancellationToken
    ) => CompleteTransactionAsync(transaction, commit: false, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            foreach (FlightSqlConnection connection in _connections.Values)
            {
                connection.Dispose();
            }

            _connections.Clear();
        }
    }

    private async IAsyncEnumerable<RecordBatch> ReadBatchesAsync(
        IReadOnlyList<FlightEndpoint> endpoints,
        TimeSpan commandTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        using CancellationTokenSource timeout = CreateTimeout(commandTimeout, cancellationToken);
        DateTime deadline = DateTime.UtcNow.Add(commandTimeout);

        foreach (FlightEndpoint endpoint in endpoints)
        {
            Uri address = ResolveEndpointAddress(endpoint);
            FlightSqlConnection connection = GetConnection(address);
            using var call = connection.Client.GetStream(
                endpoint.Ticket,
                connection.CreateHeaders(),
                deadline,
                timeout.Token
            );

            while (await call.ResponseStream.MoveNext(timeout.Token).ConfigureAwait(false))
            {
                yield return call.ResponseStream.Current;
            }
        }
    }

    private Uri ResolveEndpointAddress(FlightEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        string? location = endpoint.Locations.Select(candidate => candidate.Uri).FirstOrDefault();
        return _endpointPolicy.Resolve(location);
    }

    private FlightSqlConnection GetConnection(Uri address)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_connections.TryGetValue(address.AbsoluteUri, out FlightSqlConnection? connection))
            {
                return connection;
            }

            connection = new FlightSqlConnection(address, _userName, _password);
            _connections.Add(address.AbsoluteUri, connection);
            return connection;
        }
    }

    private static CancellationTokenSource CreateTimeout(
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        source.CancelAfter(commandTimeout);
        return source;
    }

    private async Task CompleteTransactionAsync(
        Transaction transaction,
        bool commit,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        FlightSqlConnection connection = GetConnection(_endpointPolicy.PrimaryAddress);
        using CancellationTokenSource timeout = CreateTimeout(_commandTimeout, cancellationToken);
        var request = new ActionEndTransactionRequest
        {
            TransactionId = transaction.TransactionId,
            Action = (ActionEndTransactionRequest.Types.EndTransaction)(commit ? 1 : 2),
        };
        var action = new FlightAction(EndTransactionAction, Any.Pack(request).ToByteArray());
        using AsyncServerStreamingCall<FlightResult> call = connection.Client.DoAction(
            action,
            connection.CreateHeaders(),
            DateTime.UtcNow.Add(_commandTimeout),
            timeout.Token
        );
        await foreach (
            FlightResult _ in call.ResponseStream.ReadAllAsync(timeout.Token).ConfigureAwait(false)
        ) { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
