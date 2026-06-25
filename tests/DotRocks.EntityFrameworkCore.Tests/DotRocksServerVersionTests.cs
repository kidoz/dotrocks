using DotRocks.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksServerVersionTests
{
    [Fact]
    public void Constructor_DefaultsMinorAndPatchToZero()
    {
        var version = new StarRocksServerVersion(4);

        Assert.Equal(4, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(0, version.Patch);
        Assert.Equal("4.0.0", version.ToString());
    }

    [Fact]
    public void Constructor_RejectsNegativeComponents() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StarRocksServerVersion(-1));

    [Theory]
    [InlineData("3.5.5-fd4e51b", 3, 5, 5)]
    [InlineData("4.0.7", 4, 0, 7)]
    [InlineData("4.0", 4, 0, 0)]
    [InlineData("4", 4, 0, 0)]
    public void Parse_ReadsLeadingVersionAndIgnoresSuffix(
        string input,
        int major,
        int minor,
        int patch
    )
    {
        StarRocksServerVersion version = StarRocksServerVersion.Parse(input);

        Assert.Equal(new StarRocksServerVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4")]
    public void Parse_ThrowsFormatExceptionOnUnrecognizedInput(string input) =>
        Assert.Throws<FormatException>(() => StarRocksServerVersion.Parse(input));

    [Fact]
    public void Parse_ThrowsOnEmptyInput() =>
        Assert.Throws<ArgumentException>(() => StarRocksServerVersion.Parse(string.Empty));

    [Fact]
    public void Equality_ComparesComponents()
    {
        Assert.Equal(new StarRocksServerVersion(4, 0, 7), new StarRocksServerVersion(4, 0, 7));
        Assert.NotEqual(new StarRocksServerVersion(4, 0, 7), new StarRocksServerVersion(4, 0, 8));
        Assert.Equal(
            new StarRocksServerVersion(4, 0, 7).GetHashCode(),
            new StarRocksServerVersion(4, 0, 7).GetHashCode()
        );
    }

    [Fact]
    public void ServerVersion_IsRecordedOnTheOptionsExtension()
    {
        var optionsBuilder = new DbContextOptionsBuilder();
        optionsBuilder.UseStarRocks(
            "Server=127.0.0.1;Port=9030;User ID=root",
            starRocks => starRocks.ServerVersion(new StarRocksServerVersion(4, 0, 7))
        );

        DotRocksOptionsExtension? extension =
            optionsBuilder.Options.FindExtension<DotRocksOptionsExtension>();

        Assert.NotNull(extension);
        Assert.Equal(new StarRocksServerVersion(4, 0, 7), extension!.ServerVersion);
    }

    [Fact]
    public void ServerVersion_IsNullWhenNotConfigured()
    {
        var optionsBuilder = new DbContextOptionsBuilder();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");

        DotRocksOptionsExtension? extension =
            optionsBuilder.Options.FindExtension<DotRocksOptionsExtension>();

        Assert.NotNull(extension);
        Assert.Null(extension!.ServerVersion);
    }
}
