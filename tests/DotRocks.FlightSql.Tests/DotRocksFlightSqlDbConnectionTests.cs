using System.Data;
using System.Diagnostics.CodeAnalysis;
using DotRocks.FlightSql;
using Xunit;

namespace DotRocks.FlightSql.Tests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "Await-using declarations in xUnit tests intentionally retain the test context."
)]
public sealed class DotRocksFlightSqlDbConnectionTests
{
    [Fact]
    public void Constructor_FallbackRequiresExplicitModeAndConnectionString()
    {
        DotRocksFlightSqlOptions options = CreateOptions();

        Assert.ThrowsAny<ArgumentException>(() =>
            new DotRocksFlightSqlDbConnection(options, "Server=127.0.0.1;User ID=root")
        );
        Assert.ThrowsAny<ArgumentException>(() =>
            new DotRocksFlightSqlDbConnection(
                options,
                fallbackMode: DotRocksFlightSqlFallbackMode.ReadQueries
            )
        );
    }

    [Fact]
    public void ConnectionString_RedactsFlightPassword()
    {
        using var connection = new DotRocksFlightSqlDbConnection(CreateOptions());

        Assert.DoesNotContain("secret", connection.ConnectionString, StringComparison.Ordinal);
        Assert.Contains(
            "Password=<redacted>",
            connection.ConnectionString,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task OpenAndClose_ManageLogicalStateWithoutNetworkIo()
    {
        await using var connection = new DotRocksFlightSqlDbConnection(CreateOptions());

        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(ConnectionState.Open, connection.State);

        await connection.CloseAsync().ConfigureAwait(true);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void SynchronousCommandExecution_IsRejectedExplicitly()
    {
        using var connection = new DotRocksFlightSqlDbConnection(CreateOptions());
        connection.Open();
        using DotRocksFlightSqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            command.ExecuteScalar()
        );

        Assert.Contains("asynchronous only", exception.Message, StringComparison.Ordinal);
    }

    private static DotRocksFlightSqlOptions CreateOptions() =>
        new(new Uri("grpc://127.0.0.1:9408"), "root", "secret") { AllowInsecureTransport = true };
}
