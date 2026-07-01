using System.Data.Common;
using DotRocks.Data;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class DotRocksDataSourceTests
{
    [Fact]
    public void Constructor_NormalizesConnectionString()
    {
        using var dataSource = new DotRocksDataSource(
            "Host=starrocks.local;User=alice;Pooling=true;Min Pool Size=1;Max Pool Size=2"
        );

        Assert.Contains(
            "Server=starrocks.local",
            dataSource.ConnectionString,
            StringComparison.Ordinal
        );
        Assert.Contains("User ID=alice", dataSource.ConnectionString, StringComparison.Ordinal);
        Assert.Contains("Pooling=True", dataSource.ConnectionString, StringComparison.Ordinal);
        Assert.Contains(
            "Minimum Pool Size=1",
            dataSource.ConnectionString,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Maximum Pool Size=2",
            dataSource.ConnectionString,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void CreateConnection_UsesNormalizedConnectionString()
    {
        using var dataSource = new DotRocksDataSource("Host=starrocks.local;User=alice");

        using DbConnection connection = dataSource.CreateConnection();

        var dotRocksConnection = Assert.IsType<DotRocksConnection>(connection);
        Assert.Equal(dataSource.ConnectionString, dotRocksConnection.ConnectionString);
    }

    [Fact]
    public void ConnectionString_RedactsPassword()
    {
        using var dataSource = new DotRocksDataSource(
            "Host=starrocks.local;User=alice;Password=s3cr3t-value"
        );

        Assert.DoesNotContain(
            "s3cr3t-value",
            dataSource.ConnectionString,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Server=starrocks.local",
            dataSource.ConnectionString,
            StringComparison.Ordinal
        );

        // Created connections must not leak the password through their public surface either.
        using DbConnection connection = dataSource.CreateConnection();
        Assert.DoesNotContain(
            "s3cr3t-value",
            connection.ConnectionString,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void CreateCommand_AttachesClosedDotRocksConnection()
    {
        using var dataSource = new DotRocksDataSource("Host=starrocks.local;User=alice");

        using DbCommand command = dataSource.CreateCommand("SELECT 1");

        Assert.IsType<DotRocksCommand>(command);
        Assert.Equal("SELECT 1", command.CommandText);
        Assert.IsType<DotRocksConnection>(command.Connection);
        Assert.Equal(dataSource.ConnectionString, command.Connection.ConnectionString);
    }

    [Fact]
    public async Task DisposedDataSource_RejectsNewConnectionsAndCommands()
    {
        var dataSource = new DotRocksDataSource("Host=starrocks.local;User=alice");
        await dataSource.DisposeAsync().ConfigureAwait(true);

        Assert.Throws<ObjectDisposedException>(() => dataSource.CreateConnection());
        Assert.Throws<ObjectDisposedException>(() => dataSource.OpenConnection());
        Assert.Throws<ObjectDisposedException>(() => dataSource.CreateCommand("SELECT 1"));
        await Assert
            .ThrowsAsync<ObjectDisposedException>(async () =>
                await dataSource
                    .OpenConnectionAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
    }
}
