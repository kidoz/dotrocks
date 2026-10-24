using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DotRocks.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Configures DotRocks-specific Entity Framework Core provider options.
/// </summary>
public sealed class DotRocksDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    : RelationalDbContextOptionsBuilder<DotRocksDbContextOptionsBuilder, DotRocksOptionsExtension>(
        optionsBuilder
    )
{
    /// <summary>
    /// Declares that the application serializes migrations across every process and instance,
    /// using an external lock held for the entire migration. StarRocks has no verified database
    /// migration lock; without this declaration runtime migration execution fails explicitly.
    /// This method does not acquire a lock.
    /// </summary>
    /// <returns>The same builder so calls can be chained.</returns>
    public DotRocksDbContextOptionsBuilder ExternallySerializedMigrations() =>
        WithOption(extension => extension.WithExternallySerializedMigrations());

    /// <summary>
    /// Records a StarRocks server version as provider metadata and service-provider cache identity.
    /// This setting does not change SQL generation or transport capability policy. Constructing
    /// the options never contacts the server. Discover the version once
    /// with <see cref="StarRocksServerVersion.DetectAsync(string, System.Threading.CancellationToken)"/>.
    /// </summary>
    /// <param name="serverVersion">The StarRocks server version to record.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public DotRocksDbContextOptionsBuilder ServerVersion(StarRocksServerVersion serverVersion)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);
        return WithOption(extension => extension.WithServerVersion(serverVersion));
    }
}
