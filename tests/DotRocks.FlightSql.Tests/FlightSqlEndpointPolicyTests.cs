using DotRocks.FlightSql;
using Xunit;

namespace DotRocks.FlightSql.Tests;

public sealed class FlightSqlEndpointPolicyTests
{
    [Fact]
    public void Constructor_RequiresExplicitPlaintextOptIn()
    {
        var options = new DotRocksFlightSqlOptions(new Uri("grpc://localhost:9408"), "root", "");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new DotRocksFlightSqlDataSource(options)
        );

        Assert.Contains("AllowInsecureTransport", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("grpc://starrocks.internal:9408", "http://starrocks.internal:9408/")]
    [InlineData("grpc+tcp://starrocks.internal:9408", "http://starrocks.internal:9408/")]
    [InlineData("grpc+tls://starrocks.internal:9408", "https://starrocks.internal:9408/")]
    public void Constructor_NormalizesSupportedSchemes(string endpoint, string expected)
    {
        var options = new DotRocksFlightSqlOptions(new Uri(endpoint), "root", "")
        {
            AllowInsecureTransport = true,
        };

        var policy = new FlightSqlEndpointPolicy(options);

        Assert.Equal(expected, policy.PrimaryAddress.AbsoluteUri);
    }

    [Fact]
    public void Resolve_RejectsUntrustedServerLocation()
    {
        var options = new DotRocksFlightSqlOptions(
            new Uri("grpc://frontend.internal:9408"),
            "root",
            ""
        )
        {
            AllowInsecureTransport = true,
        };
        var policy = new FlightSqlEndpointPolicy(options);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            policy.Resolve("grpc+tcp://attacker.example:9410")
        );

        Assert.DoesNotContain("attacker.example", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("grpc+unix:///tmp/backend.sock")]
    [InlineData("grpc://backend.internal:9419/tickets")]
    [InlineData("ftp://backend.internal:9419")]
    public void Resolve_RejectsUnsupportedServerLocationAsUntrusted(string location)
    {
        var options = new DotRocksFlightSqlOptions(
            new Uri("grpc://frontend.internal:9408"),
            "root",
            ""
        )
        {
            AllowInsecureTransport = true,
        };
        var policy = new FlightSqlEndpointPolicy(options);

        // A server-supplied location is untrusted input, so a malformed one must read as a
        // rejected endpoint that the data source can skip, not as a caller argument error.
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            policy.Resolve(location)
        );

        Assert.DoesNotContain("backend", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AllowsExplicitBackendHost()
    {
        var options = new DotRocksFlightSqlOptions(
            new Uri("grpc://frontend.internal:9408"),
            "root",
            ""
        )
        {
            AllowInsecureTransport = true,
            AllowedEndpointAddresses = [new Uri("grpc://backend.internal:9410")],
        };
        var policy = new FlightSqlEndpointPolicy(options);

        Uri endpoint = policy.Resolve("grpc+tcp://backend.internal:9410");

        Assert.Equal("http://backend.internal:9410/", endpoint.AbsoluteUri);
    }

    [Fact]
    public void Resolve_RejectsUntrustedPortOnTrustedHost()
    {
        var options = new DotRocksFlightSqlOptions(
            new Uri("grpc://frontend.internal:9408"),
            "root",
            ""
        )
        {
            AllowInsecureTransport = true,
            AllowedEndpointAddresses = [new Uri("grpc://backend.internal:9410")],
        };
        var policy = new FlightSqlEndpointPolicy(options);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            policy.Resolve("grpc+tcp://backend.internal:9999")
        );

        Assert.DoesNotContain("backend.internal", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("9999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsRelativeAllowedEndpointAddress()
    {
        var options = new DotRocksFlightSqlOptions(
            new Uri("grpc://frontend.internal:9408"),
            "root",
            ""
        )
        {
            AllowInsecureTransport = true,
            AllowedEndpointAddresses = [new Uri("backend.internal/flight", UriKind.Relative)],
        };

        Assert.Throws<ArgumentException>(() => new DotRocksFlightSqlDataSource(options));
    }

    [Fact]
    public void Resolve_ReusesFrontendForReuseConnectionLocation()
    {
        var options = new DotRocksFlightSqlOptions(
            new Uri("grpc+tls://frontend.internal:9408"),
            "root",
            ""
        );
        var policy = new FlightSqlEndpointPolicy(options);

        Uri endpoint = policy.Resolve("arrow-flight-reuse-connection://?");

        Assert.Same(policy.PrimaryAddress, endpoint);
    }
}
