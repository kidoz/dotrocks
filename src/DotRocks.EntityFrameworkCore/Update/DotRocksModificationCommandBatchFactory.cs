using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Update;

namespace DotRocks.EntityFrameworkCore.Update;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksModificationCommandBatchFactory(
    ModificationCommandBatchFactoryDependencies dependencies
) : IModificationCommandBatchFactory
{
    public ModificationCommandBatch Create() => new SingularModificationCommandBatch(dependencies);
}
