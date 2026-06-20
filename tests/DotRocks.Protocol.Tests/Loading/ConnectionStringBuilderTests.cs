using System.Diagnostics;
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
        Assert.False(builder.Pooling);
        Assert.Equal(0, builder.MinimumPoolSize);
        Assert.Equal(100, builder.MaximumPoolSize);
        Assert.Equal(300, builder.ConnectionIdleTimeout);
        Assert.Equal(DotRocksSslMode.Disabled, builder.SslMode);
        Assert.False(builder.TrustServerCertificate);
        Assert.False(builder.AllowInsecureStreamLoad);
        Assert.Equal("http://starrocks.local:8030/", builder.StreamLoadEndpoint);
        Assert.Contains(
            "Stream Load Endpoint=http://starrocks.local:8030/",
            builder.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ToString_RedactsPasswordAliasAndConnectionDebugSurfaceHasNoSecretDisplay()
    {
        const string secret = "debug-secret-value";
        var builder = new DotRocksConnectionStringBuilder(
            "Host=starrocks.local;User=alice;Pwd=" + secret
        );
        using var connection = new DotRocksConnection(builder.ConnectionString);

        Assert.Contains("Password=***", builder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, builder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, connection.ToString(), StringComparison.Ordinal);
        Assert.Null(
            Attribute.GetCustomAttribute(
                typeof(DotRocksConnection),
                typeof(DebuggerDisplayAttribute)
            )
        );
        Assert.Null(
            Attribute.GetCustomAttribute(
                typeof(DotRocksConnectionStringBuilder),
                typeof(DebuggerDisplayAttribute)
            )
        );
    }

    [Fact]
    public void PoolingOptions_ParseAliases()
    {
        var builder = new DotRocksConnectionStringBuilder(
            "Pooling=true;Min Pool Size=1;Max Pool Size=2;Idle Timeout=3"
        );

        Assert.True(builder.Pooling);
        Assert.Equal(1, builder.MinimumPoolSize);
        Assert.Equal(2, builder.MaximumPoolSize);
        Assert.Equal(3, builder.ConnectionIdleTimeout);
        Assert.Contains("Pooling=True", builder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPort_Throws()
    {
        var builder = new DotRocksConnectionStringBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Port = 0);
    }

    [Fact]
    public void InvalidPoolSizes_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = new DotRocksConnectionStringBuilder(
                "Minimum Pool Size=2;Maximum Pool Size=1"
            ).BuildOptions()
        );
    }

    [Fact]
    public void InvalidPoolPropertyValues_Throw()
    {
        var builder = new DotRocksConnectionStringBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.MinimumPoolSize = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.MaximumPoolSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.ConnectionIdleTimeout = 0);
    }

    [Fact]
    public void TlsOptions_ParseAliases()
    {
        var builder = new DotRocksConnectionStringBuilder(
            "SSL Mode=Required;TrustServerCertificate=true"
        );

        Assert.Equal(DotRocksSslMode.Required, builder.SslMode);
        Assert.True(builder.TrustServerCertificate);
        Assert.Contains("Ssl Mode=Required", builder.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "Trust Server Certificate=True",
            builder.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void SslRevocationCheck_DefaultsToOfflineAndParsesAliases()
    {
        var defaults = new DotRocksConnectionStringBuilder();
        Assert.Equal(
            System.Security.Cryptography.X509Certificates.X509RevocationMode.Offline,
            defaults.SslRevocationCheck
        );

        var builder = new DotRocksConnectionStringBuilder("SSL Revocation Check=Online");
        Assert.Equal(
            System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
            builder.SslRevocationCheck
        );
        Assert.Equal(
            System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
            builder.BuildOptions().SslRevocationMode
        );
    }

    [Fact]
    public void TrustServerCertificateWithoutTls_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _ = new DotRocksConnectionStringBuilder("Trust Server Certificate=true").BuildOptions()
        );
    }

    [Fact]
    public void StreamLoadEndpoint_ParsesAliases()
    {
        var builder = new DotRocksConnectionStringBuilder(
            "Host=starrocks.local;Http Endpoint=https://load.starrocks.local:8443;AllowInsecureStreamLoad=true"
        );

        Assert.Equal("https://load.starrocks.local:8443/", builder.StreamLoadEndpoint);
        Assert.True(builder.AllowInsecureStreamLoad);
        Assert.Contains(
            "Stream Load Endpoint=https://load.starrocks.local:8443/",
            builder.ToString(),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Allow Insecure Stream Load=True",
            builder.ToString(),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("ftp://starrocks.local:8030")]
    [InlineData("http://alice@starrocks.local:8030")]
    public void InvalidStreamLoadEndpoint_Throws(string endpoint)
    {
        var builder = new DotRocksConnectionStringBuilder();

        Assert.Throws<ArgumentException>(() => builder.StreamLoadEndpoint = endpoint);
    }
}
