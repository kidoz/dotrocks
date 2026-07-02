using DotRocks.Data;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

// Canonical integration-test environment. Sibling integration test projects compile this exact
// file via <Compile Include> links in their .csproj; edit this copy only.
internal static class IntegrationTestEnvironment
{
    private const string SkipReason =
        "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server.";

    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTROCKS_RUN_INTEGRATION"),
            "1",
            StringComparison.Ordinal
        );

    /// <summary>Skips the calling test unless the StarRocks integration environment is enabled.</summary>
    public static void SkipUnlessEnabled() => Assert.SkipUnless(IsEnabled, SkipReason);

    public static string ConnectionString
    {
        get
        {
            string? explicitConnectionString = Environment.GetEnvironmentVariable(
                "DOTROCKS_CONNECTION_STRING"
            );
            if (!string.IsNullOrWhiteSpace(explicitConnectionString))
            {
                return explicitConnectionString;
            }

            var builder = new DotRocksConnectionStringBuilder
            {
                Server = Environment.GetEnvironmentVariable("DOTROCKS_HOST") ?? "127.0.0.1",
                UserId = Environment.GetEnvironmentVariable("DOTROCKS_USER") ?? "root",
                Password = Environment.GetEnvironmentVariable("DOTROCKS_PASSWORD") ?? string.Empty,
                ConnectionTimeout = 30,
            };

            string? port = Environment.GetEnvironmentVariable("DOTROCKS_PORT");
            if (
                int.TryParse(
                    port,
                    System.Globalization.NumberStyles.Integer,
                    null,
                    out int parsedPort
                )
            )
            {
                builder.Port = parsedPort;
            }

            string? database = Environment.GetEnvironmentVariable("DOTROCKS_DATABASE");
            if (!string.IsNullOrWhiteSpace(database))
            {
                builder.Database = database;
            }

            string? streamLoadEndpoint = Environment.GetEnvironmentVariable(
                "DOTROCKS_STREAM_LOAD_ENDPOINT"
            );
            if (!string.IsNullOrWhiteSpace(streamLoadEndpoint))
            {
                builder.StreamLoadEndpoint = streamLoadEndpoint;
                if (builder.StreamLoadEndpoint.StartsWith("http://", StringComparison.Ordinal))
                {
                    builder.AllowInsecureStreamLoad = true;
                }
            }
            else
            {
                string? httpPort =
                    Environment.GetEnvironmentVariable("DOTROCKS_FE_HTTP_PORT")
                    ?? Environment.GetEnvironmentVariable("DOTROCKS_HTTP_PORT");
                if (
                    int.TryParse(
                        httpPort,
                        System.Globalization.NumberStyles.Integer,
                        null,
                        out int parsedHttpPort
                    )
                )
                {
                    builder.StreamLoadEndpoint = $"http://{builder.Server}:{parsedHttpPort}";
                    builder.AllowInsecureStreamLoad = true;
                }
            }

            return builder.ConnectionString;
        }
    }
}
