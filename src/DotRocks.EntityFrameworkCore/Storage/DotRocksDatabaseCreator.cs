using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data;
using DotRocks.EntityFrameworkCore.Migrations;
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
    public bool CanConnect() => Probe(existenceOnly: false);

    private bool Probe(bool existenceOnly)
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
        catch (Exception ex)
            when (IsConnectionFailure(ex) && (!existenceOnly || IsMissingDatabase(ex)))
        {
            return false;
        }
    }

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        ProbeAsync(false, cancellationToken);

    private async Task<bool> ProbeAsync(bool existenceOnly, CancellationToken cancellationToken)
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
        catch (Exception ex)
            when (IsConnectionFailure(ex) && (!existenceOnly || IsMissingDatabase(ex)))
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

    // Only an unknown database means absence; authentication and transport failures must remain errors.
    public bool Exists() => Probe(existenceOnly: true);

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) =>
        ProbeAsync(true, cancellationToken);

    private static bool IsMissingDatabase(Exception exception) =>
        exception is DotRocksException error
        && DotRocksHistoryRepository.IsUnknownDatabase(error.ServerErrorCode);

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
