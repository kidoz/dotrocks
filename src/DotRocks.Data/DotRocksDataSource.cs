using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data.Loading;
using DotRocks.Data.Pooling;

namespace DotRocks.Data;

/// <summary>
/// Represents a reusable DotRocks data source that creates logical connections using one
/// normalized connection configuration.
/// </summary>
public sealed class DotRocksDataSource : DbDataSource
{
    private readonly DotRocksConnectionOptions _options;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksDataSource"/> class.
    /// </summary>
    /// <param name="connectionString">The DotRocks connection string.</param>
    public DotRocksDataSource(string connectionString)
    {
        _options = DotRocksConnectionOptions.Parse(connectionString);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the redacted form (the password is omitted, matching
    /// <see cref="DotRocksConnection.ConnectionString"/>), so logging or echoing this property
    /// cannot leak the secret. Connections created by this data source still carry the full
    /// credentials internally.
    /// </remarks>
    public override string ConnectionString => _options.ToRedactedConnectionString();

    /// <inheritdoc />
    protected override DbConnection CreateDbConnection()
    {
        ThrowIfDisposed();
        return CreateDotRocksConnection();
    }

    /// <inheritdoc />
    protected override DbConnection OpenDbConnection()
    {
        DotRocksConnection connection = CreateDotRocksConnection();
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(
        CancellationToken cancellationToken = default
    )
    {
        DotRocksConnection connection = CreateDotRocksConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "This factory method stores caller-provided command text; execution-time parameterization remains explicit."
    )]
    protected override DbCommand CreateDbCommand(string? commandText = null)
    {
        ThrowIfDisposed();
        return new DotRocksCommand(commandText ?? string.Empty, CreateDotRocksConnection());
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isDisposed = true;
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override ValueTask DisposeAsyncCore()
    {
        _isDisposed = true;
        return base.DisposeAsyncCore();
    }

    /// <summary>
    /// Closes and removes idle physical connections from the pool for this data source's
    /// configuration. Connections currently in use are unaffected.
    /// </summary>
    public void ClearPool() => DotRocksConnectionPool.Clear(_options);

    private DotRocksConnection CreateDotRocksConnection()
    {
        ThrowIfDisposed();
        // Pass the parsed options directly: round-tripping through the public (redacted)
        // ConnectionString would silently drop the password.
        return new DotRocksConnection(_options);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
