using DotRocks.Data;
using DotRocks.FlightSql;
using Xunit;

namespace DotRocks.FlightSql.IntegrationTests;

internal static class FlightSqlIntegrationEnvironment
{
    private const string SkipReason =
        "Flight SQL integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks Flight endpoint.";

    public static void SkipUnlessEnabled() =>
        Assert.SkipUnless(
            string.Equals(
                Environment.GetEnvironmentVariable("DOTROCKS_RUN_INTEGRATION"),
                "1",
                StringComparison.Ordinal
            ),
            SkipReason
        );

    public static string MySqlConnectionString
    {
        get
        {
            var builder = new DotRocksConnectionStringBuilder
            {
                Server = Environment.GetEnvironmentVariable("DOTROCKS_HOST") ?? "127.0.0.1",
                UserId = Environment.GetEnvironmentVariable("DOTROCKS_USER") ?? "root",
                Password = Environment.GetEnvironmentVariable("DOTROCKS_PASSWORD") ?? string.Empty,
                ConnectionTimeout = 30,
            };
            if (
                int.TryParse(
                    Environment.GetEnvironmentVariable("DOTROCKS_PORT"),
                    System.Globalization.NumberStyles.Integer,
                    null,
                    out int port
                )
            )
            {
                builder.Port = port;
            }

            return builder.ConnectionString;
        }
    }

    public static DotRocksFlightSqlOptions FlightOptions
    {
        get
        {
            string host = Environment.GetEnvironmentVariable("DOTROCKS_HOST") ?? "127.0.0.1";
            string port = Environment.GetEnvironmentVariable("DOTROCKS_FE_FLIGHT_PORT") ?? "9408";
            string[] allowedHosts = (
                Environment.GetEnvironmentVariable("DOTROCKS_FLIGHT_ALLOWED_HOSTS") ?? string.Empty
            ).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new DotRocksFlightSqlOptions(
                new Uri($"grpc://{host}:{port}"),
                Environment.GetEnvironmentVariable("DOTROCKS_USER") ?? "root",
                Environment.GetEnvironmentVariable("DOTROCKS_PASSWORD") ?? string.Empty
            )
            {
                AllowInsecureTransport = true,
                AllowedEndpointHosts = allowedHosts,
                CommandTimeout = TimeSpan.FromSeconds(60),
            };
        }
    }
}
