using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data;

namespace DotRocks.FlightSql;

/// <summary>
/// Represents an asynchronous ADO.NET connection that executes commands over Arrow Flight SQL.
/// </summary>
public sealed class DotRocksFlightSqlDbConnection : DbConnection
{
    private readonly DotRocksFlightSqlDataSource _flightDataSource;
    private readonly DotRocksFlightSqlOptions _options;
    private readonly DotRocksConnection? _fallbackConnection;
    private readonly SemaphoreSlim _fallbackOpenGate = new(1, 1);
    private DotRocksFlightSqlDbTransaction? _activeTransaction;
    private ConnectionState _state;
    private int _disposed;

    /// <summary>
    /// Initializes an ADO.NET connection backed by Arrow Flight SQL.
    /// </summary>
    /// <param name="options">The Flight SQL endpoint, credentials, and endpoint policy.</param>
    /// <param name="fallbackConnectionString">
    /// An optional DotRocks MySQL-protocol connection string used only by the explicitly enabled
    /// fallback modes.
    /// </param>
    /// <param name="fallbackMode">The operations that may use the MySQL-protocol fallback.</param>
    public DotRocksFlightSqlDbConnection(
        DotRocksFlightSqlOptions options,
        string? fallbackConnectionString = null,
        DotRocksFlightSqlFallbackMode fallbackMode = DotRocksFlightSqlFallbackMode.None
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateFallbackMode(fallbackMode);
        if (fallbackMode != DotRocksFlightSqlFallbackMode.None)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fallbackConnectionString);
            _fallbackConnection = new DotRocksConnection(fallbackConnectionString);
        }
        else if (!string.IsNullOrWhiteSpace(fallbackConnectionString))
        {
            throw new ArgumentException(
                "A fallback connection string requires an explicit fallback mode.",
                nameof(fallbackConnectionString)
            );
        }

        _options = options;
        FallbackMode = fallbackMode;
        _flightDataSource = new DotRocksFlightSqlDataSource(options);
    }

    /// <summary>
    /// Gets the explicitly enabled MySQL-protocol fallback operations.
    /// </summary>
    public DotRocksFlightSqlFallbackMode FallbackMode { get; }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString
    {
        get => _options.ToString();
        set =>
            throw new NotSupportedException(
                "Configure Flight SQL connections with DotRocksFlightSqlOptions."
            );
    }

    /// <inheritdoc />
    public override string Database => _fallbackConnection?.Database ?? string.Empty;

    /// <inheritdoc />
    public override string DataSource => _options.Endpoint.Host;

    /// <inheritdoc />
    public override string ServerVersion => string.Empty;

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override int ConnectionTimeout => checked((int)_options.CommandTimeout.TotalSeconds);

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException(
            "Changing the database on a Flight SQL connection is not supported."
        );

    /// <inheritdoc />
    public override void Open()
    {
        ThrowIfDisposed();
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The Flight SQL connection is already open.");
        }

        _state = ConnectionState.Open;
    }

    /// <inheritdoc />
    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Close()
    {
        if (_state == ConnectionState.Closed)
        {
            return;
        }

        try
        {
            if (_activeTransaction is not null)
            {
                _activeTransaction
                    .RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        finally
        {
            _fallbackConnection?.Close();
            _state = ConnectionState.Closed;
        }
    }

    /// <inheritdoc />
    public override async Task CloseAsync()
    {
        if (_state == ConnectionState.Closed)
        {
            return;
        }

        try
        {
            if (_activeTransaction is not null)
            {
                await _activeTransaction
                    .RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (_fallbackConnection is not null)
            {
                await _fallbackConnection.CloseAsync().ConfigureAwait(false);
            }

            _state = ConnectionState.Closed;
        }
    }

    /// <summary>
    /// Creates a Flight SQL command associated with this connection.
    /// </summary>
    public new DotRocksFlightSqlCommand CreateCommand() => new(this);

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => CreateCommand();

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException(
            "Flight SQL transaction creation is asynchronous only. Use BeginTransactionAsync."
        );

    /// <inheritdoc />
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken
    )
    {
        EnsureOpen();
        if (_activeTransaction is not null)
        {
            throw new InvalidOperationException(
                "The Flight SQL connection already has an active transaction."
            );
        }

        if (isolationLevel is not IsolationLevel.Unspecified and not IsolationLevel.ReadCommitted)
        {
            throw new NotSupportedException(
                "Flight SQL does not expose transaction isolation selection for this transport."
            );
        }

        DotRocksFlightSqlTransaction transaction = await _flightDataSource
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var dbTransaction = new DotRocksFlightSqlDbTransaction(this, transaction, isolationLevel);
        _activeTransaction = dbTransaction;
        return dbTransaction;
    }

    internal DotRocksFlightSqlDataSource FlightDataSource => _flightDataSource;

    internal DotRocksFlightSqlDbTransaction? ActiveTransaction => _activeTransaction;

    internal void EnsureOpen()
    {
        ThrowIfDisposed();
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The Flight SQL connection is not open.");
        }
    }

    internal void ValidateTransaction(DotRocksFlightSqlDbTransaction? transaction)
    {
        if (_activeTransaction is null)
        {
            if (transaction is not null)
            {
                throw new InvalidOperationException("The command transaction has completed.");
            }

            return;
        }

        if (!ReferenceEquals(_activeTransaction, transaction))
        {
            throw new InvalidOperationException(
                "Commands must reference the connection's active Flight SQL transaction."
            );
        }
    }

    internal void ClearActiveTransaction(DotRocksFlightSqlDbTransaction transaction)
    {
        if (ReferenceEquals(_activeTransaction, transaction))
        {
            _activeTransaction = null;
        }
    }

    internal async Task<DotRocksConnection> GetFallbackConnectionAsync(
        CancellationToken cancellationToken
    )
    {
        EnsureOpen();
        if (_activeTransaction is not null)
        {
            throw new InvalidOperationException(
                "MySQL-protocol fallback cannot participate in a Flight SQL transaction."
            );
        }

        DotRocksConnection fallback =
            _fallbackConnection
            ?? throw new InvalidOperationException("No fallback connection is configured.");
        if (fallback.State == ConnectionState.Open)
        {
            return fallback;
        }

        await _fallbackOpenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (fallback.State == ConnectionState.Closed)
            {
                await fallback.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            return fallback;
        }
        finally
        {
            _fallbackOpenGate.Release();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                Close();
            }
            finally
            {
                _fallbackConnection?.Dispose();
                _flightDataSource.Dispose();
                _fallbackOpenGate.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_activeTransaction is not null)
            {
                await _activeTransaction.DisposeAsync().ConfigureAwait(false);
            }

            if (_fallbackConnection is not null)
            {
                await _fallbackConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _flightDataSource.Dispose();
            _fallbackOpenGate.Dispose();
            _state = ConnectionState.Closed;
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static void ValidateFallbackMode(DotRocksFlightSqlFallbackMode fallbackMode)
    {
        const DotRocksFlightSqlFallbackMode all =
            DotRocksFlightSqlFallbackMode.ReadQueries | DotRocksFlightSqlFallbackMode.WriteCommands;
        if ((fallbackMode & ~all) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackMode));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
