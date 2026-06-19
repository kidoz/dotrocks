using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DotRocks.EntityFrameworkCore.Migrations;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksMigrator : IMigrator
{
    public void Migrate(string? targetMigration = null) => throw CreateUnsupportedException();

    public Task MigrateAsync(
        string? targetMigration = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateUnsupportedException();
    }

    public string GenerateScript(
        string? fromMigration = null,
        string? toMigration = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    ) => throw CreateUnsupportedException();

    public bool HasPendingModelChanges() => throw CreateUnsupportedException();

    private static NotSupportedException CreateUnsupportedException() =>
        new("DotRocks EF Core migrations are not implemented yet.");
}
