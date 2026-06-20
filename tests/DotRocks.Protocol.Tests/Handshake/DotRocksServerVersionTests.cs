using DotRocks.Data.Protocol.Handshake;
using Xunit;

namespace DotRocks.Protocol.Tests.Handshake;

public sealed class DotRocksServerVersionTests
{
    [Theory]
    // Canonical StarRocks handshake strings across supported lines.
    [InlineData("8.0.33-StarRocks-3.5.4", 3, 5, 4)]
    [InlineData("8.0.33-StarRocks-4.0.7", 4, 0, 7)]
    [InlineData("5.7.43-StarRocks-3.2.11", 3, 2, 11)]
    // Future major: parsing must not be pinned to known lines.
    [InlineData("8.0.33-StarRocks-5.0.0", 5, 0, 0)]
    // Missing patch / minor default to zero.
    [InlineData("8.0.33-StarRocks-3.3", 3, 3, 0)]
    [InlineData("8.0.33-StarRocks-4", 4, 0, 0)]
    // Pre-release and build suffixes are ignored after the numeric version.
    [InlineData("8.0.33-StarRocks-4.0.0-rc01", 4, 0, 0)]
    [InlineData("8.0.33-StarRocks-3.5.4-abc1234", 3, 5, 4)]
    // Extra numeric components beyond patch are ignored.
    [InlineData("8.0.33-StarRocks-3.5.4.9", 3, 5, 4)]
    // The MySQL-compatibility prefix must never be mistaken for the version.
    [InlineData("9.9.9-StarRocks-3.5.0", 3, 5, 0)]
    // Marker casing is tolerated.
    [InlineData("8.0.33-starrocks-3.5.1", 3, 5, 1)]
    public void Parse_RecognizesStarRocksVersions(
        string serverVersion,
        int major,
        int minor,
        int patch
    )
    {
        DotRocksServerVersion version = DotRocksServerVersion.Parse(serverVersion);

        Assert.True(version.IsStarRocks);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(serverVersion, version.Raw);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8.0.33")] // plain MySQL-compatible version, no StarRocks marker
    [InlineData("5.7.0-log")]
    [InlineData("8.0.33-StarRocks")] // marker present but no numeric version
    [InlineData("8.0.33-StarRocks-")]
    [InlineData("8.0.33-StarRocks-vX")] // marker present, non-numeric version
    public void Parse_UnrecognizedStrings_AreUnknownButRetainRaw(string serverVersion)
    {
        DotRocksServerVersion version = DotRocksServerVersion.Parse(serverVersion);

        Assert.False(version.IsStarRocks);
        Assert.Equal(0, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(0, version.Patch);
        Assert.Equal(serverVersion, version.Raw);
        Assert.Equal(DotRocksServerVersion.Unknown, version);
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DotRocksServerVersion.Parse(null!));
    }

    [Fact]
    public void Parse_OverflowingComponent_StopsAtLastGoodComponent()
    {
        DotRocksServerVersion version = DotRocksServerVersion.Parse(
            "8.0.33-StarRocks-3.99999999999999999999.4"
        );

        Assert.True(version.IsStarRocks);
        Assert.Equal(3, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(0, version.Patch);
    }

    [Theory]
    [InlineData("8.0.33-StarRocks-3.5.4", "8.0.33-StarRocks-4.0.0")]
    [InlineData("8.0.33-StarRocks-4.0.0", "8.0.33-StarRocks-4.0.7")]
    [InlineData("8.0.33-StarRocks-4.0.7", "8.0.33-StarRocks-4.1.0")]
    [InlineData("8.0.33-StarRocks-4.1.0", "8.0.33-StarRocks-5.0.0")]
    public void CompareTo_OrdersByVersion(string lower, string higher)
    {
        DotRocksServerVersion low = DotRocksServerVersion.Parse(lower);
        DotRocksServerVersion high = DotRocksServerVersion.Parse(higher);

        Assert.True(low < high);
        Assert.True(high > low);
        Assert.True(low <= high);
        Assert.True(high >= low);
        Assert.NotEqual(low, high);
    }

    [Fact]
    public void Unknown_OrdersBelowAnyRecognizedVersion()
    {
        DotRocksServerVersion oldest = DotRocksServerVersion.Parse("8.0.33-StarRocks-3.0.0");

        Assert.True(DotRocksServerVersion.Unknown < oldest);
        Assert.True(oldest > DotRocksServerVersion.Unknown);
    }

    [Fact]
    public void Equality_IgnoresRawString()
    {
        DotRocksServerVersion a = DotRocksServerVersion.Parse("8.0.33-StarRocks-3.5.4");
        DotRocksServerVersion b = DotRocksServerVersion.Parse("5.7.43-StarRocks-3.5.4");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a.Raw, b.Raw);
    }

    [Fact]
    public void ToString_ReturnsRawString()
    {
        Assert.Equal(
            "8.0.33-StarRocks-3.5.4",
            DotRocksServerVersion.Parse("8.0.33-StarRocks-3.5.4").ToString()
        );
    }
}
