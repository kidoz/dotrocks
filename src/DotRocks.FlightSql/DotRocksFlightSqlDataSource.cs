using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Flight.Sql;
using Arrow.Flight.Protocol.Sql;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace DotRocks.FlightSql;

/// <summary>
/// Provides an experimental Arrow Flight SQL transport for StarRocks queries and updates.
/// </summary>
/// <remarks>
/// One data source owns one authenticated session per Flight endpoint, so sharing an instance
/// across connections and commands reuses those sessions instead of creating new ones. Dispose it
/// asynchronously where possible: <see cref="DisposeAsync" /> releases the server sessions, while
/// <see cref="Dispose" /> blocks on the same work.
/// </remarks>
public sealed class DotRocksFlightSqlDataSource : IDisposable, IAsyncDisposable
{
    private const string BeginTransactionAction = "BeginTransaction";
    private const string EndTransactionAction = "EndTransaction";
    private static readonly Schema EmptySchema = new([], null);
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
        Options = options;
    }

    internal DotRocksFlightSqlOptions Options { get; }

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
    /// Creates an ADO.NET connection that shares this data source, and therefore its channels and
    /// authenticated sessions.
    /// </summary>
    /// <returns>A connection that does not dispose this data source.</returns>
    public DotRocksFlightSqlDbConnection CreateConnection() =>
        new(this, fallbackConnectionString: null, DotRocksFlightSqlFallbackMode.None);

    /// <summary>
    /// Creates an ADO.NET connection that shares this data source and may use the MySQL-protocol
    /// fallback.
    /// </summary>
    /// <param name="fallbackConnectionString">
    /// A DotRocks MySQL-protocol connection string used only by the explicitly enabled fallback
    /// modes.
    /// </param>
    /// <param name="fallbackMode">The operations that may use the MySQL-protocol fallback.</param>
    /// <returns>A connection that does not dispose this data source.</returns>
    public DotRocksFlightSqlDbConnection CreateConnection(
        string? fallbackConnectionString,
        DotRocksFlightSqlFallbackMode fallbackMode
    ) => new(this, fallbackConnectionString, fallbackMode);

    /// <summary>
    /// Begins a Flight SQL transaction.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels transaction creation.</param>
    /// <returns>The new transaction.</returns>
    public async Task<DotRocksFlightSqlTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            ThrowIfDisposed();
            FlightSqlConnection connection = GetConnection(_endpointPolicy.PrimaryAddress);
            using CancellationTokenSource timeout = CreateTimeout(
                _commandTimeout,
                cancellationToken
            );
            Metadata headers = await connection
                .CreateHeadersAsync(timeout.Token)
                .ConfigureAwait(false);
            var request = new ActionBeginTransactionRequest();
            var action = new FlightAction(BeginTransactionAction, Any.Pack(request).ToByteArray());
            using AsyncServerStreamingCall<FlightResult> call = connection.Client.DoAction(
                action,
                headers,
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

                return new DotRocksFlightSqlTransaction(
                    this,
                    new Transaction(response.TransactionId)
                );
            }

            throw new InvalidOperationException(
                "The Flight SQL server did not return a transaction handle."
            );
        }
        catch (Exception exception) when (FlightSqlErrors.IsRemoteFailure(exception))
        {
            throw FlightSqlErrors.Sanitize(exception);
        }
    }

    internal async Task<DotRocksFlightSqlResult> ExecuteQueryAsync(
        string sql,
        Transaction transaction,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        try
        {
            ThrowIfDisposed();
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);

            FlightSqlConnection connection = GetConnection(_endpointPolicy.PrimaryAddress);
            TimeSpan effectiveTimeout = commandTimeout ?? _commandTimeout;
            using CancellationTokenSource timeout = CreateTimeout(
                effectiveTimeout,
                cancellationToken
            );
            var callOptions = new FlightCallOptions
            {
                Headers = await connection.CreateHeadersAsync(timeout.Token).ConfigureAwait(false),
            };
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
        catch (Exception exception) when (FlightSqlErrors.IsRemoteFailure(exception))
        {
            throw FlightSqlErrors.Sanitize(exception);
        }
    }

    internal async Task<long> ExecuteUpdateAsync(
        string sql,
        Transaction transaction,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        try
        {
            ThrowIfDisposed();
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            FlightSqlConnection connection = GetConnection(_endpointPolicy.PrimaryAddress);
            TimeSpan effectiveTimeout = commandTimeout ?? _commandTimeout;
            using CancellationTokenSource timeout = CreateTimeout(
                effectiveTimeout,
                cancellationToken
            );
            var update = new CommandStatementUpdate { Query = sql };
            if (transaction.IsValid)
            {
                update.TransactionId = transaction.TransactionId;
            }

            FlightDescriptor descriptor = FlightDescriptor.CreateCommandDescriptor(
                Any.Pack(update).ToByteArray()
            );
            Metadata headers = await connection
                .CreateHeadersAsync(timeout.Token)
                .ConfigureAwait(false);
            using var call = await connection
                .Client.StartPut(
                    descriptor,
                    EmptySchema,
                    headers,
                    CreateDeadline(effectiveTimeout),
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
        catch (Exception exception) when (FlightSqlErrors.IsRemoteFailure(exception))
        {
            throw FlightSqlErrors.Sanitize(exception);
        }
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
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        FlightSqlConnection[] connections;
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            connections = [.. _connections.Values];
            _connections.Clear();
        }

        foreach (FlightSqlConnection connection in connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private async IAsyncEnumerable<RecordBatch> ReadBatchesAsync(
        IReadOnlyList<FlightEndpoint> endpoints,
        TimeSpan commandTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        using CancellationTokenSource timeout = CreateTimeout(commandTimeout, cancellationToken);
        DateTime? deadline = CreateDeadline(commandTimeout);

        foreach (FlightEndpoint endpoint in endpoints)
        {
            EndpointStream stream = await OpenEndpointStreamAsync(endpoint, deadline, timeout.Token)
                .ConfigureAwait(false);
            using FlightRecordBatchStreamingCall call = stream.Call;
            bool hasBatch = stream.HasBatch;
            while (hasBatch)
            {
                yield return call.ResponseStream.Current;
                hasBatch = await FlightSqlErrors
                    .ReadNextAsync(call.ResponseStream, timeout.Token)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Opens the ticket stream on the first trusted endpoint location that answers.
    /// </summary>
    private async Task<EndpointStream> OpenEndpointStreamAsync(
        FlightEndpoint endpoint,
        DateTime? deadline,
        CancellationToken cancellationToken
    )
    {
        List<Uri> addresses = ResolveEndpointAddresses(endpoint);
        for (int index = 0; ; index++)
        {
            bool isLastAddress = index == addresses.Count - 1;
            FlightRecordBatchStreamingCall? call = null;
            try
            {
                FlightSqlConnection connection = GetConnection(addresses[index]);
                Metadata headers = await connection
                    .CreateHeadersAsync(cancellationToken)
                    .ConfigureAwait(false);
                call = connection.Client.GetStream(
                    endpoint.Ticket,
                    headers,
                    deadline,
                    cancellationToken
                );
                bool hasBatch = await call
                    .ResponseStream.MoveNext(cancellationToken)
                    .ConfigureAwait(false);
                return new EndpointStream(call, hasBatch);
            }
            catch (RpcException exception)
                when (!isLastAddress && exception.StatusCode == StatusCode.Unavailable)
            {
                // A backend that does not answer must not hide the remaining trusted locations.
                call?.Dispose();
            }
            catch (Exception exception) when (FlightSqlErrors.IsRemoteFailure(exception))
            {
                call?.Dispose();
                throw FlightSqlErrors.Sanitize(exception);
            }
            catch
            {
                // Any other failure ends the read; the opened call must not outlive it.
                call?.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Returns the trusted addresses advertised for an endpoint, in server-supplied order.
    /// </summary>
    private List<Uri> ResolveEndpointAddresses(FlightEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var addresses = new List<Uri>();
        Exception? rejection = null;
        foreach (FlightLocation location in endpoint.Locations)
        {
            Uri address;
            try
            {
                address = _endpointPolicy.Resolve(location.Uri);
            }
            catch (InvalidOperationException exception)
            {
                // A single untrusted location must not disqualify the trusted alternatives.
                rejection = exception;
                continue;
            }

            if (!addresses.Contains(address))
            {
                addresses.Add(address);
            }
        }

        if (addresses.Count != 0)
        {
            return addresses;
        }

        return rejection is null ? [_endpointPolicy.PrimaryAddress] : throw rejection;
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

            connection = new FlightSqlConnection(address, _userName, _password, _commandTimeout);
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
        if (commandTimeout != Timeout.InfiniteTimeSpan)
        {
            source.CancelAfter(commandTimeout);
        }

        return source;
    }

    private static DateTime? CreateDeadline(TimeSpan commandTimeout) =>
        commandTimeout == Timeout.InfiniteTimeSpan ? null : DateTime.UtcNow.Add(commandTimeout);

    private async Task CompleteTransactionAsync(
        Transaction transaction,
        bool commit,
        CancellationToken cancellationToken
    )
    {
        try
        {
            ThrowIfDisposed();
            FlightSqlConnection connection = GetConnection(_endpointPolicy.PrimaryAddress);
            using CancellationTokenSource timeout = CreateTimeout(
                _commandTimeout,
                cancellationToken
            );
            Metadata headers = await connection
                .CreateHeadersAsync(timeout.Token)
                .ConfigureAwait(false);
            var request = new ActionEndTransactionRequest
            {
                TransactionId = transaction.TransactionId,
                Action = (ActionEndTransactionRequest.Types.EndTransaction)(commit ? 1 : 2),
            };
            var action = new FlightAction(EndTransactionAction, Any.Pack(request).ToByteArray());
            using AsyncServerStreamingCall<FlightResult> call = connection.Client.DoAction(
                action,
                headers,
                DateTime.UtcNow.Add(_commandTimeout),
                timeout.Token
            );
            await foreach (
                FlightResult _ in call
                    .ResponseStream.ReadAllAsync(timeout.Token)
                    .ConfigureAwait(false)
            ) { }
        }
        catch (Exception exception) when (FlightSqlErrors.IsRemoteFailure(exception))
        {
            throw FlightSqlErrors.Sanitize(exception);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private readonly record struct EndpointStream(
        FlightRecordBatchStreamingCall Call,
        bool HasBatch
    );
}
