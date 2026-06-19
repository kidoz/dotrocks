using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DotRocks.EntityFrameworkCore.Infrastructure;

internal sealed class DotRocksOptionsExtension : RelationalOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public DotRocksOptionsExtension() { }

    private DotRocksOptionsExtension(DotRocksOptionsExtension copyFrom)
        : base(copyFrom) { }

    public override DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    protected override RelationalOptionsExtension Clone() => new DotRocksOptionsExtension(this);

    public override void ApplyServices(IServiceCollection services) =>
        services.AddEntityFrameworkDotRocks();

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension)
        : RelationalExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => true;

        private new DotRocksOptionsExtension Extension => (DotRocksOptionsExtension)base.Extension;

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);
            debugInfo["DotRocks:Provider"] = "1";
        }

        public override string LogFragment =>
            string.IsNullOrEmpty(Extension.ConnectionString)
                ? "using DotRocks "
                : "using DotRocks connection string ";
    }
}
