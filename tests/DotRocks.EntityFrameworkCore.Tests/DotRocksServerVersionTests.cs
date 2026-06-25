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

    [Theory]
    [InlineData(3, 5, 0, 4, 0, 0)] // major dominates
    [InlineData(4, 0, 7, 4, 1, 0)] // minor dominates
    [InlineData(4, 0, 7, 4, 0, 8)] // patch dominates
    public void Comparison_OrdersByMajorThenMinorThenPatch(
        int lMajor,
        int lMinor,
        int lPatch,
        int rMajor,
        int rMinor,
        int rPatch
    )
    {
        var lower = new StarRocksServerVersion(lMajor, lMinor, lPatch);
        var higher = new StarRocksServerVersion(rMajor, rMinor, rPatch);

        Assert.True(lower < higher);
        Assert.True(lower <= higher);
        Assert.True(higher > lower);
        Assert.True(higher >= lower);
        Assert.True(lower.CompareTo(higher) < 0);
        Assert.Equal(0, lower.CompareTo(new StarRocksServerVersion(lMajor, lMinor, lPatch)));
    }

    [Fact]
    public void Comparison_EnablesVersionGating()
    {
        StarRocksServerVersion detected = StarRocksServerVersion.Parse("4.0.7");

        Assert.True(detected >= new StarRocksServerVersion(3, 5));
        Assert.False(detected >= new StarRocksServerVersion(4, 1));
    }

    [Fact]
    public void Comparison_TreatsNullAsEarliest()
    {
        var version = new StarRocksServerVersion(1);

        Assert.True(version > null);
        Assert.True(null < version);
        Assert.Equal(1, version.CompareTo(null));
        Assert.True((StarRocksServerVersion?)null == (StarRocksServerVersion?)null);
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
