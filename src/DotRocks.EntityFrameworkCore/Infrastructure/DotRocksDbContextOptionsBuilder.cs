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
    /// Configures the StarRocks server version the provider targets. The version is recorded
    /// in the options only; constructing the options never contacts the server. Discover it once
    /// with <see cref="StarRocksServerVersion.DetectAsync(string, System.Threading.CancellationToken)"/>.
    /// </summary>
    /// <param name="serverVersion">The StarRocks server version to target.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public DotRocksDbContextOptionsBuilder ServerVersion(StarRocksServerVersion serverVersion)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);
        return WithOption(extension => extension.WithServerVersion(serverVersion));
    }
}
