using System.Data.Common;
using DotRocks.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Extension methods for configuring DotRocks as an Entity Framework Core provider.
/// </summary>
public static class DotRocksDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Configures the context to use DotRocks for StarRocks.
    /// </summary>
    /// <param name="optionsBuilder">The context options builder.</param>
    /// <param name="connectionString">The DotRocks connection string.</param>
    /// <param name="dotRocksOptionsAction">The optional DotRocks provider options action.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static DbContextOptionsBuilder UseStarRocks(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        Action<DotRocksDbContextOptionsBuilder>? dotRocksOptionsAction = null
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var extension =
            GetOrCreateExtension(optionsBuilder).WithConnectionString(connectionString)
            as DotRocksOptionsExtension;
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension!);
        dotRocksOptionsAction?.Invoke(new DotRocksDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    /// <summary>
    /// Configures the context to use DotRocks for StarRocks.
    /// </summary>
    /// <param name="optionsBuilder">The context options builder.</param>
    /// <param name="connection">The DotRocks connection.</param>
    /// <param name="contextOwnsConnection">A value indicating whether the context owns the connection.</param>
    /// <param name="dotRocksOptionsAction">The optional DotRocks provider options action.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static DbContextOptionsBuilder UseStarRocks(
        this DbContextOptionsBuilder optionsBuilder,
        DbConnection connection,
        bool contextOwnsConnection = false,
        Action<DotRocksDbContextOptionsBuilder>? dotRocksOptionsAction = null
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connection);

        var extension =
            GetOrCreateExtension(optionsBuilder).WithConnection(connection, contextOwnsConnection)
            as DotRocksOptionsExtension;
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension!);
        dotRocksOptionsAction?.Invoke(new DotRocksDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    /// <summary>
    /// Configures the context to use DotRocks for StarRocks.
    /// </summary>
    /// <typeparam name="TContext">The type of context being configured.</typeparam>
    /// <param name="optionsBuilder">The context options builder.</param>
    /// <param name="connectionString">The DotRocks connection string.</param>
    /// <param name="dotRocksOptionsAction">The optional DotRocks provider options action.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseStarRocks<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        Action<DotRocksDbContextOptionsBuilder>? dotRocksOptionsAction = null
    )
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)
            UseStarRocks(
                (DbContextOptionsBuilder)optionsBuilder,
                connectionString,
                dotRocksOptionsAction
            );

    /// <summary>
    /// Configures the context to use DotRocks for StarRocks.
    /// </summary>
    /// <typeparam name="TContext">The type of context being configured.</typeparam>
    /// <param name="optionsBuilder">The context options builder.</param>
    /// <param name="connection">The DotRocks connection.</param>
    /// <param name="contextOwnsConnection">A value indicating whether the context owns the connection.</param>
    /// <param name="dotRocksOptionsAction">The optional DotRocks provider options action.</param>
    /// <returns>The same builder so calls can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseStarRocks<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        DbConnection connection,
        bool contextOwnsConnection = false,
        Action<DotRocksDbContextOptionsBuilder>? dotRocksOptionsAction = null
    )
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)
            UseStarRocks(
                (DbContextOptionsBuilder)optionsBuilder,
                connection,
                contextOwnsConnection,
                dotRocksOptionsAction
            );

    private static DotRocksOptionsExtension GetOrCreateExtension(
        DbContextOptionsBuilder optionsBuilder
    ) =>
        optionsBuilder.Options.FindExtension<DotRocksOptionsExtension>()
        ?? new DotRocksOptionsExtension();
}
