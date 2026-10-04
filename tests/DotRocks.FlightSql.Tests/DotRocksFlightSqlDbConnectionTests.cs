using System.Data;
using System.Diagnostics.CodeAnalysis;
using Apache.Arrow.Flight.Sql;
using DotRocks.FlightSql;
using Google.Protobuf;
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

    [Fact]
    public void Close_WhenImplicitRollbackFails_LeavesTheConnectionClosed()
    {
        using var dataSource = new DotRocksFlightSqlDataSource(CreateOptions());
        using var connection = dataSource.CreateConnection();
        connection.Open();
        AdoptFailingTransaction(connection, dataSource);
        dataSource.Dispose();

        connection.Close();

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Null(connection.ActiveTransaction);
    }

    [Fact]
    public async Task DisposeAsync_WhenImplicitRollbackFails_LeavesTheConnectionClosed()
    {
        await using var dataSource = new DotRocksFlightSqlDataSource(CreateOptions());
        await using var connection = dataSource.CreateConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        AdoptFailingTransaction(connection, dataSource);
        await dataSource.DisposeAsync().ConfigureAwait(true);

        await connection.DisposeAsync().ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Null(connection.ActiveTransaction);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Close and DisposeAsync take ownership of the adopted transaction. Disposing it here would skip the rollback path under test."
    )]
    private static void AdoptFailingTransaction(
        DotRocksFlightSqlDbConnection connection,
        DotRocksFlightSqlDataSource dataSource
    )
    {
        var transaction = new DotRocksFlightSqlTransaction(
            dataSource,
            new Transaction(ByteString.CopyFrom([0x01]))
        );
        connection.AdoptActiveTransaction(
            new DotRocksFlightSqlDbTransaction(connection, transaction, IsolationLevel.Unspecified)
        );
    }

    private static DotRocksFlightSqlOptions CreateOptions() =>
        new(new Uri("grpc://127.0.0.1:9408"), "root", "secret") { AllowInsecureTransport = true };
}
