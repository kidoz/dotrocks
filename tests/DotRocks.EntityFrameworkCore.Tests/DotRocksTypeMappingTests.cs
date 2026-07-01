using System.Diagnostics.CodeAnalysis;
using DotRocks.Data;
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
    [InlineData("largeint", typeof(Int128))]
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
    public void FindMapping_BareDecimalUsesNonZeroScaleDefault()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        RelationalTypeMapping? mapping = source.FindMapping(typeof(decimal));

        // A bare decimal must not default to DECIMAL(10,0) (which would truncate the scale).
        Assert.NotNull(mapping);
        Assert.Equal(typeof(decimal), mapping.ClrType);
        Assert.NotNull(mapping.Scale);
        Assert.True(mapping.Scale > 0);
        Assert.NotNull(mapping.Precision);
        // Stays within System.Decimal's representable range.
        Assert.True(mapping.Precision <= 29);
    }

    [Fact]
    public void FindMapping_MapsHighPrecisionDecimalToDotRocksDecimal()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        RelationalTypeMapping? mapping = source.FindMapping("decimal(38, 18)");

        Assert.NotNull(mapping);
        Assert.Equal(typeof(DotRocksDecimal), mapping.ClrType);
        Assert.Equal(38, mapping.Precision);
        Assert.Equal(18, mapping.Scale);
    }

    [Fact]
    public void FindMapping_MapsDotRocksDecimalClrType()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        RelationalTypeMapping? mapping = source.FindMapping(typeof(DotRocksDecimal));

        Assert.NotNull(mapping);
        Assert.Equal(typeof(DotRocksDecimal), mapping.ClrType);
    }

    [Fact]
    public void FindMapping_MapsInt128ClrType()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        RelationalTypeMapping? mapping = source.FindMapping(typeof(Int128));

        Assert.NotNull(mapping);
        Assert.Equal(typeof(Int128), mapping.ClrType);
        Assert.Equal("largeint", mapping.StoreType);
    }

    [Fact]
    public void FindMapping_RejectsUnsignedInt128()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        Assert.Null(source.FindMapping(typeof(UInt128)));
    }

    [Fact]
    public void FindMapping_RejectsBinaryUntilStarRocksBinaryBehaviorIsVerified()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        Assert.Null(source.FindMapping("varbinary"));
    }

    [Theory]
    [InlineData("varchar")]
    [InlineData("json")]
    public void StringMapping_EscapesBackslashInLiteral(string storeType)
    {
        IRelationalTypeMappingSource source = CreateMappingSource();
        RelationalTypeMapping mapping = source.FindMapping(typeof(string), storeType)!;

        // StarRocks treats backslash as an escape character; a trailing backslash must be doubled
        // or 'a\' would consume the closing quote (literal corruption / injection).
        Assert.Equal("'a\\\\'", mapping.GenerateSqlLiteral("a\\"));
    }

    [Fact]
    public void StringMapping_EscapesQuotesAndControlCharactersInLiteral()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();
        RelationalTypeMapping mapping = source.FindMapping(typeof(string), "varchar")!;

        Assert.Equal("'a''b'", mapping.GenerateSqlLiteral("a'b"));
        Assert.Equal("'line\\nbreak'", mapping.GenerateSqlLiteral("line\nbreak"));
    }

    [Fact]
    public void GuidMapping_GeneratesQuotedLiteral()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();
        RelationalTypeMapping mapping = source.FindMapping(typeof(Guid))!;

        var value = Guid.Parse("9f4f591e-3db2-4879-856c-1c54b4241b76");
        Assert.Equal("'9f4f591e-3db2-4879-856c-1c54b4241b76'", mapping.GenerateSqlLiteral(value));
    }

    [Fact]
    public void HighPrecisionDecimalProperty_AddsDecimalConverter()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        // A model property typed System.Decimal must carry a decimal<->DotRocksDecimal converter so
        // reads materialize into the decimal property instead of failing on a type mismatch.
        RelationalTypeMapping mapping = source.FindMapping(typeof(decimal), "decimal(38, 18)")!;

        Assert.Equal(typeof(decimal), mapping.ClrType);
        Assert.NotNull(mapping.Converter);
        Assert.Equal(typeof(decimal), mapping.Converter!.ModelClrType);
        Assert.Equal(typeof(DotRocksDecimal), mapping.Converter.ProviderClrType);
        Assert.Equal("28.90", mapping.GenerateSqlLiteral(28.90m));
    }

    [Fact]
    public void HighPrecisionDotRocksDecimalProperty_HasNoConverter()
    {
        IRelationalTypeMappingSource source = CreateMappingSource();

        // A DotRocksDecimal-typed property already matches the provider type; it must not get a
        // converter (that would double-convert).
        RelationalTypeMapping mapping = source.FindMapping(
            typeof(DotRocksDecimal),
            "decimal(38, 18)"
        )!;

        Assert.Equal(typeof(DotRocksDecimal), mapping.ClrType);
        Assert.Null(mapping.Converter);
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
