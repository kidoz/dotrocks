using System.Data.Common;
using DotRocks.Data;
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
    /// <exception cref="ArgumentException">
    /// The connection string is missing, empty, or cannot be parsed as a DotRocks connection
    /// string. The failure surfaces at registration so a configuration mistake does not turn
    /// into an obscure error on first context use.
    /// </exception>
    public static DbContextOptionsBuilder UseStarRocks(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        Action<DotRocksDbContextOptionsBuilder>? dotRocksOptionsAction = null
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ValidateConnectionString(connectionString);

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

    // A missing or malformed connection string must fail here, at registration, with a
    // configuration-oriented message — not later as an opaque failure deep inside first
    // context use.
    private static void ValidateConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "The DotRocks connection string is not configured. Pass a non-empty connection "
                    + "string to UseStarRocks, for example "
                    + "\"Server=<host>;Port=9030;User ID=<user>;Password=<password>\".",
                nameof(connectionString)
            );
        }

        try
        {
            // Round-tripping through the builder runs the full DotRocks connection string
            // parser and option validation without opening a connection.
            _ = new DotRocksConnectionStringBuilder(connectionString).ToString();
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw new ArgumentException(
                "The DotRocks connection string is invalid: "
                    + exception.Message
                    + " See the DotRocks connection-strings documentation for the supported "
                    + "keywords and values.",
                nameof(connectionString),
                exception
            );
        }
    }
}
