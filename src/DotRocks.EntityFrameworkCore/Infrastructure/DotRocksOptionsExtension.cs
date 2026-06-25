using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DotRocks.EntityFrameworkCore.Infrastructure;

/// <summary>
/// The DotRocks Entity Framework Core options extension. Public because the relational options
/// builder base type exposes it as a generic argument; application code does not use it directly.
/// </summary>
public sealed class DotRocksOptionsExtension : RelationalOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;
    private StarRocksServerVersion? _serverVersion;

    /// <summary>Initializes a new instance of the <see cref="DotRocksOptionsExtension"/> class.</summary>
    public DotRocksOptionsExtension() { }

    private DotRocksOptionsExtension(DotRocksOptionsExtension copyFrom)
        : base(copyFrom)
    {
        _serverVersion = copyFrom._serverVersion;
    }

    /// <inheritdoc />
    public override DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <summary>Gets the explicitly configured StarRocks server version, if any.</summary>
    public StarRocksServerVersion? ServerVersion => _serverVersion;

    /// <summary>Returns a copy of this extension with the given StarRocks server version.</summary>
    public DotRocksOptionsExtension WithServerVersion(StarRocksServerVersion serverVersion)
    {
        var clone = (DotRocksOptionsExtension)Clone();
        clone._serverVersion = serverVersion;
        return clone;
    }

    /// <inheritdoc />
    protected override RelationalOptionsExtension Clone() => new DotRocksOptionsExtension(this);

    /// <inheritdoc />
    public override void ApplyServices(IServiceCollection services) =>
        services.AddEntityFrameworkDotRocks();

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension)
        : RelationalExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => true;

        private new DotRocksOptionsExtension Extension => (DotRocksOptionsExtension)base.Extension;

        public override int GetServiceProviderHashCode() =>
            Extension.ServerVersion?.GetHashCode() ?? 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo info
            && Equals(info.Extension.ServerVersion, Extension.ServerVersion);

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);
            debugInfo["DotRocks:Provider"] = "1";
            if (Extension.ServerVersion is { } serverVersion)
            {
                debugInfo["DotRocks:ServerVersion"] = serverVersion.ToString();
            }
        }

        public override string LogFragment =>
            string.IsNullOrEmpty(Extension.ConnectionString)
                ? "using DotRocks "
                : "using DotRocks connection string ";
    }
}
