using DotRocks.Data.Protocol.Handshake;
using Xunit;

namespace DotRocks.Protocol.Tests.Handshake;

public sealed class DotRocksServerCapabilitiesTests
{
    // Golden capability sets per StarRocks line. Columns mirror the introduced-in thresholds:
    // HttpSqlApi (3.2), MySqlProtocolTls (3.4.1), SqlTransactions (3.5), StreamLoadPreparedTimeout
    // (3.5.4), MultiTableStreamLoadTransaction (4.0), Decimal256 (4.0).
    [Theory]
    //                        http  tls    sqlTx  prepTo multiTbl dec256
    [InlineData("3.1.0", false, false, false, false, false, false)]
    [InlineData("3.2.0", true, false, false, false, false, false)]
    [InlineData("3.3.5", true, false, false, false, false, false)]
    [InlineData("3.4.0", true, false, false, false, false, false)]
    [InlineData("3.4.1", true, true, false, false, false, false)]
    [InlineData("3.5.0", true, true, true, false, false, false)]
    [InlineData("3.5.3", true, true, true, false, false, false)]
    [InlineData("3.5.4", true, true, true, true, false, false)]
    [InlineData("4.0.0", true, true, true, true, true, true)]
    [InlineData("4.0.7", true, true, true, true, true, true)]
    [InlineData("4.1.0", true, true, true, true, true, true)]
    // Forward compatibility: an unreleased future line gains every additive capability.
    [InlineData("5.0.0", true, true, true, true, true, true)]
    [InlineData("9.9.9", true, true, true, true, true, true)]
    public void For_DerivesCapabilitiesPerLine(
        string starRocksVersion,
        bool httpSqlApi,
        bool tls,
        bool sqlTransactions,
        bool streamLoadPreparedTimeout,
        bool multiTableStreamLoadTransaction,
        bool decimal256
    )
    {
        DotRocksServerVersion version = DotRocksServerVersion.Parse(
            $"8.0.33-StarRocks-{starRocksVersion}"
        );

        DotRocksServerCapabilities capabilities = DotRocksServerCapabilities.For(version);

        Assert.Equal(httpSqlApi, capabilities.SupportsHttpSqlApi);
        Assert.Equal(tls, capabilities.SupportsMySqlProtocolTls);
        Assert.Equal(sqlTransactions, capabilities.SupportsSqlTransactions);
        Assert.Equal(streamLoadPreparedTimeout, capabilities.SupportsStreamLoadPreparedTimeout);
        Assert.Equal(
            multiTableStreamLoadTransaction,
            capabilities.SupportsMultiTableStreamLoadTransaction
        );
        Assert.Equal(decimal256, capabilities.SupportsDecimal256);
        Assert.Equal(version, capabilities.ServerVersion);
    }

    [Theory]
    [InlineData("8.0.33")] // no StarRocks marker
    [InlineData("")]
    [InlineData("8.0.33-StarRocks-")] // marker but no version
    public void For_UnrecognizedVersion_DisablesEveryCapability(string serverVersion)
    {
        DotRocksServerVersion version = DotRocksServerVersion.Parse(serverVersion);

        DotRocksServerCapabilities capabilities = DotRocksServerCapabilities.For(version);

        Assert.False(capabilities.SupportsHttpSqlApi);
        Assert.False(capabilities.SupportsMySqlProtocolTls);
        Assert.False(capabilities.SupportsSqlTransactions);
        Assert.False(capabilities.SupportsStreamLoadPreparedTimeout);
        Assert.False(capabilities.SupportsMultiTableStreamLoadTransaction);
        Assert.False(capabilities.SupportsDecimal256);
        Assert.False(capabilities.IsServerVersionOverridden);
    }

    [Fact]
    public void For_CompatibilityOverride_GatesOnOverrideButReportsDetectedVersion()
    {
        DotRocksServerVersion detected = DotRocksServerVersion.Parse("8.0.33-StarRocks-3.3.5");
        DotRocksServerVersion pinned = DotRocksServerVersion.ForStarRocks(4, 0, 0);

        DotRocksServerCapabilities capabilities = DotRocksServerCapabilities.For(detected, pinned);

        // Gates follow the override...
        Assert.True(capabilities.SupportsMultiTableStreamLoadTransaction);
        Assert.True(capabilities.SupportsDecimal256);
        Assert.True(capabilities.IsServerVersionOverridden);
        Assert.Equal(pinned, capabilities.EffectiveVersion);
        // ...but the reported server version stays honest.
        Assert.Equal(detected, capabilities.ServerVersion);
    }

    [Fact]
    public void For_CompatibilityOverrideDownward_ConstrainsCapabilities()
    {
        DotRocksServerVersion detected = DotRocksServerVersion.Parse("8.0.33-StarRocks-4.0.7");
        DotRocksServerVersion pinned = DotRocksServerVersion.ForStarRocks(3, 3, 0);

        DotRocksServerCapabilities capabilities = DotRocksServerCapabilities.For(detected, pinned);

        Assert.False(capabilities.SupportsMultiTableStreamLoadTransaction);
        Assert.False(capabilities.SupportsDecimal256);
        Assert.True(capabilities.IsServerVersionOverridden);
        Assert.Equal(detected, capabilities.ServerVersion);
    }

    [Theory]
    [InlineData("4", 4, 0, 0)]
    [InlineData("4.0", 4, 0, 0)]
    [InlineData("3.5.4", 3, 5, 4)]
    public void TryParseLevel_ParsesBareVersions(string value, int major, int minor, int patch)
    {
        Assert.True(DotRocksServerVersion.TryParseLevel(value, out DotRocksServerVersion version));
        Assert.True(version.IsStarRocks);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("4.x")]
    [InlineData("-1")]
    [InlineData("1.2.3.4")]
    [InlineData("StarRocks-4.0")]
    public void TryParseLevel_RejectsInvalidValues(string? value)
    {
        Assert.False(DotRocksServerVersion.TryParseLevel(value, out DotRocksServerVersion version));
        Assert.False(version.IsStarRocks);
    }
}
