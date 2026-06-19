using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksTypeMappingTests
{
    [Theory]
    [InlineData("boolean", typeof(bool))]
    [InlineData("tinyint", typeof(sbyte))]
    [InlineData("smallint", typeof(short))]
    [InlineData("int", typeof(int))]
    [InlineData("integer", typeof(int))]
    [InlineData("mediumint", typeof(int))]
    [InlineData("bigint", typeof(long))]
    [InlineData("date", typeof(DateOnly))]
    [InlineData("datetime", typeof(DateTime))]
    [InlineData("varchar(128)", typeof(string))]
    [InlineData("json", typeof(string))]
    public void FindMapping_MapsCommonStarRocksStoreTypes(string storeType, Type clrType)
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        RelationalTypeMapping? mapping = source.FindMapping(storeType);

        Assert.NotNull(mapping);
        Assert.Equal(clrType, mapping.ClrType);
    }

    [Fact]
    public void FindMapping_PreservesSupportedDecimalPrecisionAndScale()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        RelationalTypeMapping? mapping = source.FindMapping("decimal(18, 2)");

        Assert.NotNull(mapping);
        Assert.Equal(typeof(decimal), mapping.ClrType);
        Assert.Equal(18, mapping.Precision);
        Assert.Equal(2, mapping.Scale);
    }

    [Fact]
    public void FindMapping_RejectsHighPrecisionDecimalUntilLosslessEfMappingExists()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        RelationalTypeMapping? mapping = source.FindMapping("decimal(38, 18)");

        Assert.Null(mapping);
    }

    [Fact]
    public void FindMapping_RejectsLargeIntUntilInt128MappingIsImplemented()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        Assert.Null(source.FindMapping("largeint"));
        Assert.Null(source.FindMapping(typeof(Int128)));
    }

    private static IRelationalTypeMappingSource CreateMappingSource()
    {
        using var context = CreateContext();
        return context.GetService<IRelationalTypeMappingSource>();
    }

    private static UnitContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<UnitContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");
        return new UnitContext(optionsBuilder.Options);
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class UnitContext(DbContextOptions<UnitContext> options) : DbContext(options);
}
