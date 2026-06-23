using System.Diagnostics.CodeAnalysis;
using DotRocks.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksDbContextOptionsBuilderExtensionsTests
{
    private const string ConnectionString = "Server=127.0.0.1;Port=9030;User ID=root";

    [Fact]
    public void UseStarRocks_WithConnectionString_PreservesGenericBuilderForChaining()
    {
        var optionsBuilder = new DbContextOptionsBuilder<SampleContext>();

        // The generic overload must return DbContextOptionsBuilder<TContext> so the
        // documented fluent chain into .Options keeps the typed result. This also asserts
        // the return type at compile time.
        DbContextOptions<SampleContext> options = optionsBuilder
            .UseStarRocks(ConnectionString)
            .Options;

        Assert.True(HasDotRocksExtension(options));
    }

    [Fact]
    public void UseStarRocks_WithConnection_PreservesGenericBuilderForChaining()
    {
        using var connection = new DotRocksConnection(ConnectionString);
        var optionsBuilder = new DbContextOptionsBuilder<SampleContext>();

        DbContextOptions<SampleContext> options = optionsBuilder.UseStarRocks(connection).Options;

        Assert.True(HasDotRocksExtension(options));
    }

    [Fact]
    public void UseStarRocks_GenericOverload_ConfiguresSameExtensionAsNonGeneric()
    {
        var genericBuilder = new DbContextOptionsBuilder<SampleContext>();
        genericBuilder.UseStarRocks(ConnectionString);

        var nonGenericBuilder = new DbContextOptionsBuilder();
        nonGenericBuilder.UseStarRocks(ConnectionString);

        Assert.True(HasDotRocksExtension(genericBuilder.Options));
        Assert.True(HasDotRocksExtension(nonGenericBuilder.Options));
    }

    private static bool HasDotRocksExtension(DbContextOptions options) =>
        options.Extensions.Any(extension =>
            string.Equals(
                extension.GetType().Name,
                "DotRocksOptionsExtension",
                StringComparison.Ordinal
            )
        );

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The tests reference this context only as a generic type argument."
    )]
    private sealed class SampleContext(DbContextOptions<SampleContext> options)
        : DbContext(options);
}
