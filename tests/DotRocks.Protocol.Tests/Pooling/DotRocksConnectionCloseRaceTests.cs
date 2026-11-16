using System.Net.Sockets;
using DotRocks.Data;
using DotRocks.Data.Pooling;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.Pooling;

public sealed class DotRocksConnectionCloseRaceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReopenAfterCancellation_ReusesSubsequentCleanLease(bool pooling)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = FakeStarRocksServer.Start(
            async stream =>
            {
                await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
                await FakeStarRocksServer
                    .ReadCommandAndStallMidResultSetAsync(stream, release.Task, "1")
                    .ConfigureAwait(true);
            },
            ServeCleanLeaseAsync,
            HoldConnectionOpenAsync
        );
        string connectionString =
            server.ConnectionString + $";Pooling={pooling};Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        try
        {
            using var connection = new DotRocksConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(true);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT value FROM stalled";
                command.CommandTimeout = 0;
                using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(true);
                Assert.True(await reader.ReadAsync(ct).ConfigureAwait(true));
                Task<bool> stalledRead = reader.ReadAsync(ct);
                command.Cancel();
                await Assert
                    .ThrowsAnyAsync<OperationCanceledException>(() => stalledRead)
                    .ConfigureAwait(true);
            }
            release.TrySetResult();

            await connection.OpenAsync(ct).ConfigureAwait(true);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT 1";
                Assert.Equal("1", await command.ExecuteScalarAsync(ct).ConfigureAwait(true));
            }
            await connection.CloseAsync().ConfigureAwait(true);
            if (pooling)
            {
                Assert.Equal(1, DotRocksConnectionPool.GetPool(options).IdleCount);
            }
            await connection.OpenAsync(ct).ConfigureAwait(true);
            await connection.CloseAsync().ConfigureAwait(true);
            Assert.Equal(pooling ? 2 : 3, server.ConnectionCount);
        }
        finally
        {
            release.TrySetResult();
            DotRocksConnectionPool.Clear(options);
        }
    }

    private static async Task ServeCleanLeaseAsync(NetworkStream stream)
    {
        await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
        await FakeStarRocksServer
            .ReadCommandAndReplyResultSetAsync(stream, "1")
            .ConfigureAwait(true);
        // Keep the socket alive so liveness polling cannot mask the regression.
        var buffer = new byte[1];
        Assert.Equal(
            0,
            await stream
                .ReadAsync(buffer, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact]
    public async Task CloseAndAbort_ConcurrentCalls_ReturnThePoolLeaseOnce()
    {
        const int iterations = 24;
        using var server = FakeStarRocksServer.Start(
            Enumerable
                .Repeat<Func<NetworkStream, Task>>(HoldConnectionOpenAsync, iterations)
                .ToArray()
        );
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        try
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                using var connection = new DotRocksConnection(connectionString);
                await connection
                    .OpenAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                using var started = new Barrier(2);
                CancellationToken cancellationToken = TestContext.Current.CancellationToken;
                await Task.WhenAll(
                        Task.Run(
                            () =>
                            {
                                started.SignalAndWait(cancellationToken);
                                connection.Close();
                            },
                            cancellationToken
                        ),
                        Task.Run(
                            () =>
                            {
                                started.SignalAndWait(cancellationToken);
                                connection.Abort();
                            },
                            cancellationToken
                        )
                    )
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(true);

                DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.InRange(pool.IdleCount, 0, 1);
            }
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
        }
    }

    [Fact]
    public async Task CancelDuringStalledRead_DoesNotDoubleReturnThePoolLease()
    {
        const int iterations = 4;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var server = FakeStarRocksServer.Start(async stream =>
            {
                await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
                await FakeStarRocksServer
                    .ReadCommandAndStallMidResultSetAsync(stream, release.Task, "1")
                    .ConfigureAwait(true);
            });
            string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
            DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
            try
            {
                using var connection = new DotRocksConnection(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM stalled";
                command.CommandTimeout = 0;
                using var reader = await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(true);
                Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));

                Task<bool> stalledRead = reader.ReadAsync(cancellationToken);
                command.Cancel();
                await Assert
                    .ThrowsAnyAsync<OperationCanceledException>(() => stalledRead)
                    .ConfigureAwait(true);

                DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
                Assert.Equal(0, pool.ActiveLeaseCount);
            }
            finally
            {
                release.TrySetResult();
                DotRocksConnectionPool.Clear(options);
            }
        }
    }

    private static async Task HoldConnectionOpenAsync(NetworkStream stream)
    {
        await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
        var buffer = new byte[1];
        try
        {
            while (await stream.ReadAsync(buffer).ConfigureAwait(true) > 0) { }
        }
        catch (IOException)
        {
            // The client closed or the pool discarded the socket.
        }
        catch (ObjectDisposedException)
        {
            // Server teardown disposed the accepted stream.
        }
    }
}
