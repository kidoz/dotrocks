using DotRocks.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace DotRocks.EntityFrameworkCore.Design;

/// <summary>
/// Registers DotRocks Entity Framework Core design-time services.
/// </summary>
public sealed class DotRocksDesignTimeServices : IDesignTimeServices
{
    /// <inheritdoc />
    public void ConfigureDesignTimeServices(IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        serviceCollection.AddEntityFrameworkDotRocks();
    }
}
