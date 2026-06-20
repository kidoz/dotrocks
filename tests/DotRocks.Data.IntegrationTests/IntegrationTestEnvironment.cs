using DotRocks.Data;

namespace DotRocks.Data.IntegrationTests;

internal static class IntegrationTestEnvironment
{
    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTROCKS_RUN_INTEGRATION"),
            "1",
            StringComparison.Ordinal
        );

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
