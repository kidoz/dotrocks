using System.Text;
using DotRocks.FlightSql;
using Xunit;

namespace DotRocks.FlightSql.Tests;

public sealed class FlightSqlSecurityTests
{
    [Fact]
    public void Options_ToString_RedactsPassword()
    {
        var options = new DotRocksFlightSqlOptions(
            new Uri("grpc+tls://starrocks.internal:9408"),
            "app",
            "top-secret"
        );

        string text = options.ToString();

        Assert.DoesNotContain("top-secret", text, StringComparison.Ordinal);
        Assert.Contains("Password=<redacted>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_ToString_RedactsEndpointUserInformationBeforeValidation()
    {
        var options = new DotRocksFlightSqlOptions(
            new Uri("grpc+tls://endpoint-user:endpoint-secret@starrocks.internal:9408"),
            "app",
            "option-secret"
        );

        string text = options.ToString();

        Assert.DoesNotContain("endpoint-user", text, StringComparison.Ordinal);
        Assert.DoesNotContain("endpoint-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("option-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BasicAuthorization_UsesUtf8Credentials()
    {
        string header = FlightSqlConnection.CreateBasicAuthorizationValue("üser", "päss");
        string encoded = header["Basic ".Length..];

        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

        Assert.Equal("üser:päss", decoded);
    }

    [Fact]
    public void DataSource_RejectsColonInUserName()
    {
        var options = new DotRocksFlightSqlOptions(
            new Uri("grpc+tls://starrocks.internal:9408"),
            "invalid:user",
            "password"
        );

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new DotRocksFlightSqlDataSource(options)
        );

        Assert.DoesNotContain("password", exception.Message, StringComparison.Ordinal);
    }
}
