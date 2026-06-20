namespace DotRocks.Data.Loading;

/// <summary>
/// Represents an active StarRocks Stream Load transaction.
/// </summary>
public sealed class DotRocksStreamLoadTransaction
{
    private readonly DotRocksStreamLoadClient _client;
    private readonly string _databaseName;
    private readonly string _defaultTableName;
    private readonly DotRocksStreamLoadTransactionOptions _options;
    private DotRocksStreamLoadTransactionState _state = DotRocksStreamLoadTransactionState.Active;

    internal DotRocksStreamLoadTransaction(
        DotRocksStreamLoadClient client,
        string databaseName,
        string defaultTableName,
        DotRocksStreamLoadTransactionOptions options,
        DotRocksStreamLoadResult beginResult
    )
    {
        _client = client;
        _databaseName = databaseName;
        _defaultTableName = defaultTableName;
        _options = options;
        BeginResult = beginResult;
        Label = options.GetRequiredLabel();
    }

    /// <summary>
    /// Gets the transaction label.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the database name associated with the transaction.
    /// </summary>
    public string DatabaseName => _databaseName;

    /// <summary>
    /// Gets the default table name associated with the transaction.
    /// </summary>
    public string DefaultTableName => _defaultTableName;

    /// <summary>
    /// Gets the result returned by the transaction begin operation.
    /// </summary>
    public DotRocksStreamLoadResult BeginResult { get; }

    /// <summary>
    /// Loads a CSV payload stream into the transaction's default table.
    /// </summary>
    /// <param name="payload">The CSV payload stream.</param>
    /// <param name="options">The optional load options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The successful load result.</returns>
    public Task<DotRocksStreamLoadResult> LoadCsvAsync(
        Stream payload,
        DotRocksStreamLoadOptions? options = null,
        CancellationToken cancellationToken = default
    ) => LoadCsvAsync(_defaultTableName, payload, options, cancellationToken);

    /// <summary>
    /// Loads a CSV payload stream into a transaction table.
    /// </summary>
    /// <param name="tableName">The destination table name.</param>
    /// <param name="payload">The CSV payload stream.</param>
    /// <param name="options">The optional load options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The successful load result.</returns>
    public async Task<DotRocksStreamLoadResult> LoadCsvAsync(
        string tableName,
        Stream payload,
        DotRocksStreamLoadOptions? options = null,
        CancellationToken cancellationToken = default
    ) =>
        await LoadAsync(
                DotRocksStreamLoadFormat.Csv,
                tableName,
                payload,
                options,
                "text/csv",
                cancellationToken
            )
            .ConfigureAwait(false);

    /// <summary>
    /// Loads a JSON payload stream into the transaction's default table.
    /// </summary>
    /// <param name="payload">The JSON payload stream.</param>
    /// <param name="options">The optional load options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The successful load result.</returns>
    public Task<DotRocksStreamLoadResult> LoadJsonAsync(
        Stream payload,
        DotRocksStreamLoadOptions? options = null,
        CancellationToken cancellationToken = default
    ) => LoadJsonAsync(_defaultTableName, payload, options, cancellationToken);

    /// <summary>
    /// Loads a JSON payload stream into a transaction table.
    /// </summary>
    /// <param name="tableName">The destination table name.</param>
    /// <param name="payload">The JSON payload stream.</param>
    /// <param name="options">The optional load options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The successful load result.</returns>
    public async Task<DotRocksStreamLoadResult> LoadJsonAsync(
        string tableName,
        Stream payload,
        DotRocksStreamLoadOptions? options = null,
        CancellationToken cancellationToken = default
    ) =>
        await LoadAsync(
                DotRocksStreamLoadFormat.Json,
                tableName,
                payload,
                options,
                "application/json",
                cancellationToken
            )
            .ConfigureAwait(false);

    /// <summary>
    /// Pre-commits the transaction.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The successful prepare result.</returns>
    public async Task<DotRocksStreamLoadResult> PrepareAsync(
        CancellationToken cancellationToken = default
    )
    {
        EnsureState(DotRocksStreamLoadTransactionState.Active, "prepare");
        try
        {
            DotRocksStreamLoadResult result = await _client
                .SendTransactionOperationAsync(
                    "prepare",
                    _options.BuildPrepareHeaders(_databaseName),
                    cancellationToken
                )
                .ConfigureAwait(false);
            _state = DotRocksStreamLoadTransactionState.Prepared;
            return result;
        }
        catch
        {
            _state = DotRocksStreamLoadTransactionState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Commits a prepared transaction.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The successful commit result.</returns>
    public async Task<DotRocksStreamLoadResult> CommitAsync(
        CancellationToken cancellationToken = default
    )
    {
        EnsureState(DotRocksStreamLoadTransactionState.Prepared, "commit");
        return await CompleteAsync(
                "commit",
                DotRocksStreamLoadTransactionState.Committed,
                serverFailureIsInDoubt: true,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back the transaction.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The successful rollback result.</returns>
    public async Task<DotRocksStreamLoadResult> RollbackAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (
            _state
            is not DotRocksStreamLoadTransactionState.Active
                and not DotRocksStreamLoadTransactionState.Prepared
        )
        {
            throw CreateInvalidStateException("rollback");
        }

        return await CompleteAsync(
                "rollback",
                DotRocksStreamLoadTransactionState.RolledBack,
                serverFailureIsInDoubt: false,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<DotRocksStreamLoadResult> LoadAsync(
        DotRocksStreamLoadFormat format,
        string tableName,
        Stream payload,
        DotRocksStreamLoadOptions? options,
        string mediaType,
        CancellationToken cancellationToken
    )
    {
        EnsureState(DotRocksStreamLoadTransactionState.Active, "load data");
        options ??= new DotRocksStreamLoadOptions();
        try
        {
            return await _client
                .SendTransactionLoadAsync(
                    _databaseName,
                    tableName,
                    payload,
                    _options.BuildLoadHeaders(_databaseName, tableName, options, format),
                    mediaType,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch
        {
            _state = DotRocksStreamLoadTransactionState.Failed;
            throw;
        }
    }

    private async Task<DotRocksStreamLoadResult> CompleteAsync(
        string operation,
        DotRocksStreamLoadTransactionState completedState,
        bool serverFailureIsInDoubt,
        CancellationToken cancellationToken
    )
    {
        bool requestDispatched = false;
        try
        {
            DotRocksStreamLoadResult result = await _client
                .SendTransactionOperationAsync(
                    operation,
                    _options.BuildCompletionHeaders(_databaseName),
                    cancellationToken,
                    () => requestDispatched = true
                )
                .ConfigureAwait(false);
            _state = completedState;
            return result;
        }
        catch (Exception ex) when (requestDispatched && IsInDoubtTransportException(ex))
        {
            _state = DotRocksStreamLoadTransactionState.CompletionInDoubt;
            throw new DotRocksStreamLoadTransactionInDoubtException(Label, operation, ex);
        }
        catch (DotRocksStreamLoadException ex) when (serverFailureIsInDoubt && requestDispatched)
        {
            // A commit that reaches the server but returns a non-success status is genuinely
            // indeterminate: StarRocks may have applied it. Surface it as in-doubt so callers
            // reconcile by label rather than assuming the commit did not happen.
            _state = DotRocksStreamLoadTransactionState.CompletionInDoubt;
            throw new DotRocksStreamLoadTransactionInDoubtException(Label, operation, ex);
        }
        catch
        {
            _state = DotRocksStreamLoadTransactionState.Failed;
            throw;
        }
    }

    private static bool IsInDoubtTransportException(Exception exception) =>
        exception
            is OperationCanceledException
                or HttpRequestException
                or IOException
                or ObjectDisposedException;

    private void EnsureState(DotRocksStreamLoadTransactionState requiredState, string operation)
    {
        if (_state != requiredState)
        {
            throw CreateInvalidStateException(operation);
        }
    }

    private InvalidOperationException CreateInvalidStateException(string operation) =>
        new($"Cannot {operation} a Stream Load transaction in state '{_state}'.");
}

internal enum DotRocksStreamLoadTransactionState
{
    Active,
    Prepared,
    Committed,
    RolledBack,
    Failed,
    CompletionInDoubt,
}
