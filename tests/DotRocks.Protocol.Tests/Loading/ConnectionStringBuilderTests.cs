using System.Diagnostics;
using DotRocks.Data;
using DotRocks.Data.Loading;
using DotRocks.Data.Protocol.Handshake;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class ConnectionStringBuilderTests
{
    [Fact]
    public void PoolKey_ToString_RedactsPassword()
    {
        DotRocksConnectionPoolKey key = DotRocksConnectionOptions
            .Parse("Server=h;User ID=alice;Password=topsecret;Database=db")
            .CreatePoolKey();

        string text = key.ToString();

        Assert.DoesNotContain("topsecret", text, StringComparison.Ordinal);
        Assert.Contains("Password = ***", text, StringComparison.Ordinal);
        // Equality must still use the real password (pool identity is unaffected by redaction).
        Assert.Equal(
            key,
            DotRocksConnectionOptions
                .Parse("Server=h;User ID=alice;Password=topsecret;Database=db")
                .CreatePoolKey()
        );
    }

    [Fact]
    public void Connection_ConnectionStringGetter_OmitsPassword()
    {
        using var connection = new DotRocksConnection(
            "Server=h;User ID=alice;Password=topsecret;Database=db"
        );

        // The ADO ConnectionString getter follows PersistSecurityInfo=false: the password is
        // omitted entirely (not even masked), so logging or echoing it cannot leak the secret.
        string roundTripped = connection.ConnectionString;

        Assert.DoesNotContain("topsecret", roundTripped, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", roundTripped, StringComparison.OrdinalIgnoreCase);
        // Non-secret settings survive the round trip so the getter stays useful for diagnostics.
        Assert.Contains("alice", roundTripped, StringComparison.Ordinal);
        Assert.Contains("db", roundTripped, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionOptions_ToString_RedactsPasswordAndConnectionString()
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=h;User ID=alice;Password=topsecret;Database=db"
        );

        // The default record ToString would print Password and the cleartext ConnectionString;
        // the override must redact so neither leaks through interpolation, logging, or a debugger.
        string text = options.ToString();

        Assert.DoesNotContain("topsecret", text, StringComparison.Ordinal);
        Assert.Contains("Password=***", text, StringComparison.Ordinal);
    }

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
        Assert.Equal(DotRocksSslMode.Preferred, builder.SslMode);
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
    public void ConnectionRetries_DefaultsAndParses()
    {
        var defaults = new DotRocksConnectionStringBuilder();
        Assert.Equal(0, defaults.ConnectionRetries);

        var builder = new DotRocksConnectionStringBuilder(
            "Connection Retries=3;Connection Retry Delay=75"
        );
        Assert.Equal(3, builder.ConnectionRetries);
        Assert.Equal(3, builder.BuildOptions().MaxConnectionRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(75), builder.BuildOptions().ConnectionRetryDelay);
    }

    [Fact]
    public void ConnectionLifetime_DefaultsParsesAliasesAndRoundTrips()
    {
        var defaults = new DotRocksConnectionStringBuilder();
        Assert.Equal(0, defaults.ConnectionLifetime);
        Assert.Equal(TimeSpan.Zero, defaults.BuildOptions().ConnectionLifetime);

        var builder = new DotRocksConnectionStringBuilder("Lifetime=120");
        Assert.Equal(120, builder.ConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(120), builder.BuildOptions().ConnectionLifetime);

        DotRocksConnectionOptions roundTripped = DotRocksConnectionOptions.Parse(
            builder.BuildOptions().ConnectionString
        );
        Assert.Equal(TimeSpan.FromSeconds(120), roundTripped.ConnectionLifetime);
    }

    [Fact]
    public void NegativeConnectionLifetime_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = new DotRocksConnectionStringBuilder("Connection Lifetime=-1").BuildOptions()
        );
    }

    [Fact]
    public void TrustServerCertificateWithSslDisabled_Throws()
    {
        // Trusting a certificate is meaningless when TLS is disabled; it must be rejected.
        Assert.Throws<ArgumentException>(() =>
            _ = new DotRocksConnectionStringBuilder(
                "Ssl Mode=Disabled;Trust Server Certificate=true"
            ).BuildOptions()
        );
    }

    [Fact]
    public void TrustServerCertificateWithPreferredSsl_Throws()
    {
        // Under Preferred the trust flag would be a silent no-op on plaintext fallback, so bypassing
        // certificate validation must commit to TLS via Ssl Mode=Required.
        Assert.Throws<ArgumentException>(() =>
            _ = new DotRocksConnectionStringBuilder(
                "Ssl Mode=Preferred;Trust Server Certificate=true"
            ).BuildOptions()
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

    [Fact]
    public void StreamLoadEndpoint_DefaultFollowsServerUntilExplicitEndpointConfigured()
    {
        var builder = new DotRocksConnectionStringBuilder();

        Assert.Equal("http://127.0.0.1:8030/", builder.StreamLoadEndpoint);

        builder.Server = "fe.starrocks.local";

        Assert.Equal("http://fe.starrocks.local:8030/", builder.StreamLoadEndpoint);

        builder.StreamLoadEndpoint = "https://load.starrocks.local:8443";
        builder.Server = "other-fe.starrocks.local";

        Assert.Equal("https://load.starrocks.local:8443/", builder.StreamLoadEndpoint);
    }

    [Fact]
    public void Parse_StreamLoadEndpointAliases_RoundTripThroughCanonicalConnectionString()
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Host=starrocks.local;Http Endpoint=https://load.starrocks.local:8443;AllowInsecureStreamLoad=true"
        );

        DotRocksConnectionOptions reparsed = DotRocksConnectionOptions.Parse(
            options.ConnectionString
        );

        Assert.Equal(new Uri("https://load.starrocks.local:8443/"), options.StreamLoadEndpoint);
        Assert.True(options.AllowInsecureStreamLoad);
        Assert.Equal(options.StreamLoadEndpoint, reparsed.StreamLoadEndpoint);
        Assert.Equal(options.AllowInsecureStreamLoad, reparsed.AllowInsecureStreamLoad);
        Assert.Contains(
            "Stream Load Endpoint=https://load.starrocks.local:8443/",
            options.ConnectionString,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Allow Insecure Stream Load=True",
            options.ConnectionString,
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

    [Theory]
    [InlineData("ftp://starrocks.local:8030")]
    [InlineData("http://alice@starrocks.local:8030")]
    public void Parse_InvalidStreamLoadEndpoint_Throws(string endpoint)
    {
        Assert.Throws<ArgumentException>(() =>
            DotRocksConnectionOptions.Parse($"Stream Load Endpoint={endpoint}")
        );
    }

    [Fact]
    public void Parse_NoCompatibilityLevel_IsNull()
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse("Server=h;User ID=a");

        Assert.Null(options.ServerCompatibilityLevel);
    }

    [Theory]
    [InlineData("Server=h;User ID=a;Server Compatibility Level=4.0")]
    [InlineData("Server=h;User ID=a;ServerCompatibilityLevel=4.0")]
    [InlineData("Server=h;User ID=a;Compatibility Level=4.0")]
    public void Parse_CompatibilityLevel_AcceptsCanonicalAndAliases(string connectionString)
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);

        Assert.Equal(DotRocksServerVersion.ForStarRocks(4, 0, 0), options.ServerCompatibilityLevel);
    }

    [Fact]
    public void Parse_InvalidCompatibilityLevel_Throws()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            DotRocksConnectionOptions.Parse("Server=h;User ID=a;Server Compatibility Level=four")
        );

        Assert.Contains("Server Compatibility Level", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CompatibilityLevel_RoundTripsThroughCanonicalString()
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=h;User ID=a;Server Compatibility Level=3.5.4"
        );

        DotRocksConnectionOptions reparsed = DotRocksConnectionOptions.Parse(
            options.ConnectionString
        );

        Assert.Equal(
            DotRocksServerVersion.ForStarRocks(3, 5, 4),
            reparsed.ServerCompatibilityLevel
        );
    }

    [Fact]
    public void PoolKey_DiffersByCompatibilityLevel()
    {
        DotRocksConnectionPoolKey withOverride = DotRocksConnectionOptions
            .Parse("Server=h;User ID=a;Server Compatibility Level=4.0")
            .CreatePoolKey();
        DotRocksConnectionPoolKey withoutOverride = DotRocksConnectionOptions
            .Parse("Server=h;User ID=a")
            .CreatePoolKey();

        Assert.NotEqual(withoutOverride, withOverride);
        Assert.Contains("(auto)", withoutOverride.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            withOverride,
            DotRocksConnectionOptions
                .Parse("Server=h;User ID=a;Server Compatibility Level=4.0")
                .CreatePoolKey()
        );
    }
}
