using DotRocks.Data;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class ConnectionStringBuilderTests
{
    [Fact]
    public void ToString_RedactsPassword()
    {
        var builder = new DotRocksConnectionStringBuilder(
            "Host=starrocks.local;Port=9031;User=alice;Password=secret;Database=warehouse;Timeout=7"
        );

        string sanitized = builder.ToString();

        Assert.DoesNotContain("secret", sanitized, StringComparison.Ordinal);
        Assert.Contains("Password=***", sanitized, StringComparison.Ordinal);
        Assert.Equal("starrocks.local", builder.Server);
        Assert.Equal(9031, builder.Port);
        Assert.Equal("alice", builder.UserId);
        Assert.Equal("warehouse", builder.Database);
        Assert.Equal(7, builder.ConnectionTimeout);
    }

    [Fact]
    public void InvalidPort_Throws()
    {
        var builder = new DotRocksConnectionStringBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Port = 0);
    }
}
