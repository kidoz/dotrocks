using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace DotRocks.EntityFrameworkCore.Storage;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksDatabase(
    DatabaseDependencies dependencies,
    RelationalDatabaseDependencies relationalDependencies
) : RelationalDatabase(dependencies, relationalDependencies)
{
    public override int SaveChanges(IList<IUpdateEntry> entries) =>
        throw CreateUnsupportedException();

    public override Task<int> SaveChangesAsync(
        IList<IUpdateEntry> entries,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateUnsupportedException();
    }

    private static NotSupportedException CreateUnsupportedException() =>
        new("DotRocks EF Core SaveChanges is not implemented yet.");
}
