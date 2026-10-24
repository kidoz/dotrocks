using System.Data;
using System.Data.Common;
using DotRocks.Data;
using DotRocks.Data.Pooling;
using DotRocks.Protocol.Tests.TestInfrastructure;
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
    public void CreateCommand_OwnsItsConnection()
    {
        using var dataSource = new DotRocksDataSource("Host=starrocks.local;User=alice");
        using DbCommand command = dataSource.CreateCommand("SELECT 1");

        Assert.Equal("SELECT 1", command.CommandText);
        Assert.Throws<NotSupportedException>(() => command.Connection);
        Assert.Throws<NotSupportedException>(() => command.Transaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA1849:Call async methods when in an async method",
        Justification = "The theory deliberately verifies synchronous and asynchronous ADO.NET execution paths."
    )]
    public async Task CreateCommand_ExecutesAndReturnsConnectionAfterEveryOperation(
        bool asynchronous
    )
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer
                .CompleteAuthenticationVerifyingScrambleAsync(stream)
                .ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer
                .ReadCommandAndReplyResultSetAsync(stream, "scalar")
                .ConfigureAwait(true);
            _ = await FakeStarRocksServer
                .ReadCommandAndReplyResultSetAsync(stream, "row")
                .ConfigureAwait(true);
            await release.Task.WaitAsync(ct).ConfigureAwait(true);
        });
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            using var dataSource = new DotRocksDataSource(connectionString);
            DbCommand command = dataSource.CreateCommand("SELECT 1");
            await using (command.ConfigureAwait(true))
            {
                Assert.Equal(
                    0,
                    asynchronous
                        ? await command.ExecuteNonQueryAsync(ct).ConfigureAwait(true)
                        : command.ExecuteNonQuery()
                );
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(1, pool.IdleCount);
                Assert.Equal(
                    "scalar",
                    asynchronous
                        ? await command.ExecuteScalarAsync(ct).ConfigureAwait(true)
                        : command.ExecuteScalar()
                );
                Assert.Equal(0, pool.ActiveLeaseCount);

                DbDataReader reader = asynchronous
                    ? await command.ExecuteReaderAsync(ct).ConfigureAwait(true)
                    : command.ExecuteReader();
                Assert.Equal(1, pool.ActiveLeaseCount);
                if (asynchronous)
                {
                    Assert.True(await reader.ReadAsync(ct).ConfigureAwait(true));
                    Assert.Equal("row", reader.GetString(0));
                    await reader.DisposeAsync().ConfigureAwait(true);
                }
                else
                {
                    Assert.True(reader.Read());
                    Assert.Equal("row", reader.GetString(0));
                    reader.Dispose();
                }

                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(1, pool.IdleCount);
            }
            Assert.Equal(1, server.ConnectionCount);
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateCommand_DisposalReleasesAnActiveReaderConnection(bool asynchronous)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer
                .ReadCommandAndReplyResultSetAsync(stream, "row")
                .ConfigureAwait(true);
            await release.Task.WaitAsync(ct).ConfigureAwait(true);
        });
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            using var dataSource = new DotRocksDataSource(connectionString);
            DbCommand command = dataSource.CreateCommand("SELECT 1");
            using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(true);
            Assert.Equal(1, pool.ActiveLeaseCount);
            if (asynchronous)
            {
                await command.DisposeAsync().ConfigureAwait(true);
            }
            else
            {
                command.Dispose();
            }

            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(0, pool.IdleCount);
            Assert.True(reader.IsClosed);
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateCommand_ExecutionFailureReturnsItsConnection(
        bool reader,
        bool asynchronous
    )
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer
                .ReadCommandAndReplyErrorAsync(stream)
                .ConfigureAwait(true);
            await release.Task.WaitAsync(ct).ConfigureAwait(true);
        });
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            using var dataSource = new DotRocksDataSource(connectionString);
            using DbCommand command = dataSource.CreateCommand("SELECT broken");
            if (asynchronous)
            {
                if (reader)
                {
                    await Assert
                        .ThrowsAsync<DotRocksException>(() => command.ExecuteReaderAsync(ct))
                        .ConfigureAwait(true);
                }
                else
                {
                    await Assert
                        .ThrowsAsync<DotRocksException>(() => command.ExecuteNonQueryAsync(ct))
                        .ConfigureAwait(true);
                }
            }
            else if (reader)
            {
                Assert.Throws<DotRocksException>(() => command.ExecuteReader());
            }
            else
            {
                Assert.Throws<DotRocksException>(() => command.ExecuteNonQuery());
            }

            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(1, pool.IdleCount);
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Clone_PreservesCredentialsWithoutCopyingSessionState()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer
                .CompleteAuthenticationVerifyingScrambleAsync(stream)
                .ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });
        using var original = new DotRocksConnection(server.ConnectionString);
        using var clone = Assert.IsType<DotRocksConnection>(((ICloneable)original).Clone());
        Assert.NotSame(original, clone);
        Assert.Equal(ConnectionState.Closed, clone.State);
        Assert.DoesNotContain(
            FakeStarRocksServer.Secret,
            clone.ConnectionString,
            StringComparison.Ordinal
        );
        await clone.OpenAsync(ct).ConfigureAwait(true);
        using DbCommand command = clone.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Equal(0, await command.ExecuteNonQueryAsync(ct).ConfigureAwait(true));
        Assert.Equal(ConnectionState.Closed, original.State);
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
