using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace DotRocks.EntityFrameworkCore.Storage;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksDatabaseCreator(IRelationalConnection connection)
    : IDatabaseCreator,
        IRelationalDatabaseCreator
{
    public bool CanConnect()
    {
        try
        {
            bool opened = connection.Open(errorsExpected: true);
            if (opened)
            {
                connection.Close();
            }

            return true;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            return false;
        }
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            bool opened = await connection
                .OpenAsync(cancellationToken, errorsExpected: true)
                .ConfigureAwait(false);
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            return false;
        }
    }

    public void Create() => throw CreateUnsupportedException();

    public Task CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateUnsupportedException();
    }

    public void CreateTables() => throw CreateUnsupportedException();

    public Task CreateTablesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateUnsupportedException();
    }

    public void Delete() => throw CreateUnsupportedException();

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateUnsupportedException();
    }

    public bool EnsureCreated() => throw CreateUnsupportedException();

    public Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateUnsupportedException();
    }

    public bool EnsureDeleted() => throw CreateUnsupportedException();

    public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateUnsupportedException();
    }

    // StarRocks rejects login when the connection's default database does not exist, so for a
    // database-scoped connection "can connect" is equivalent to "the database exists". Probing
    // information_schema is both unnecessary and unsafe here (it would issue a command that
    // violates the active migration transaction's command-enlistment guard).
    public bool Exists() => CanConnect();

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) =>
        CanConnectAsync(cancellationToken);

    public string GenerateCreateScript() => throw CreateUnsupportedException();

    public bool HasTables() => throw CreateUnsupportedException();

    public Task<bool> HasTablesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateUnsupportedException();
    }

    private static NotSupportedException CreateUnsupportedException() =>
        new("DotRocks EF Core schema creation and deletion are not implemented yet.");

    private static bool IsConnectionFailure(Exception exception) =>
        exception is DotRocksException or DbException or TimeoutException;
}
