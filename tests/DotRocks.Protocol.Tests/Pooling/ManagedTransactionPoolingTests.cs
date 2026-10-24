using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data;
using DotRocks.Data.Pooling;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.Pooling;

public sealed class ManagedTransactionPoolingTests
{
    [Theory]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("rollback", false)]
    [InlineData("rollback", true)]
    [InlineData("dispose", false)]
    [InlineData("dispose", true)]
    [SuppressMessage(
        "Usage",
        "CA1849:Call async methods when in an async method",
        Justification = "The theory deliberately verifies synchronous and asynchronous transaction completion."
    )]
    public async Task SuccessfulManagedCompletion_ReusesPhysicalConnection(
        string completion,
        bool asynchronous
    )
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer
                .ReadCommandAndReplyResultSetAsync(stream, "101")
                .ConfigureAwait(true);
            Assert.Equal(
                "START TRANSACTION",
                await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true)
            );
            Assert.Equal(
                "INSERT INTO t VALUES (1)",
                await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true)
            );
            Assert.Equal(
                completion == "commit" ? "COMMIT WORK" : "ROLLBACK WORK",
                await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true)
            );
            _ = await FakeStarRocksServer
                .ReadCommandAndReplyResultSetAsync(stream, "101")
                .ConfigureAwait(true);
            await release.Task.WaitAsync(ct).ConfigureAwait(true);
        });
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            string? firstId;
            using (var first = new DotRocksConnection(connectionString))
            {
                await first.OpenAsync(ct).ConfigureAwait(true);
                firstId = await ReadConnectionIdAsync(first, ct).ConfigureAwait(true);
                DbTransaction transaction = await first
                    .BeginTransactionAsync(ct)
                    .ConfigureAwait(true);
                using (DbCommand command = first.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO t VALUES (1)";
                    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
                }

                if (completion == "commit")
                {
                    if (asynchronous)
                    {
                        await transaction.CommitAsync(ct).ConfigureAwait(true);
                    }
                    else
                    {
                        transaction.Commit();
                    }
                }
                else if (completion == "rollback")
                {
                    if (asynchronous)
                    {
                        await transaction.RollbackAsync(ct).ConfigureAwait(true);
                    }
                    else
                    {
                        transaction.Rollback();
                    }
                }

                if (asynchronous)
                {
                    await transaction.DisposeAsync().ConfigureAwait(true);
                }
                else
                {
                    transaction.Dispose();
                }
            }

            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(1, pool.IdleCount);
            using var next = new DotRocksConnection(connectionString);
            await next.OpenAsync(ct).ConfigureAwait(true);
            Assert.Equal(firstId, await ReadConnectionIdAsync(next, ct).ConfigureAwait(true));
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
    public async Task SuccessfulCommit_PreservesSessionDirtiness(bool mutateInsideTransaction)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            for (int i = 0; i < 3; i++)
            {
                _ = await FakeStarRocksServer
                    .ReadCommandAndReplyOkAsync(stream)
                    .ConfigureAwait(true);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], ct).ConfigureAwait(true));
            closed.TrySetResult();
        });
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            using (var connection = new DotRocksConnection(connectionString))
            {
                await connection.OpenAsync(ct).ConfigureAwait(true);
                using DbCommand command = connection.CreateCommand();
                command.CommandText = "SET @tenant = 7";
                if (!mutateInsideTransaction)
                {
                    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
                }
                DbTransaction transaction = await connection
                    .BeginTransactionAsync(ct)
                    .ConfigureAwait(true);
                await using var transactionScope = transaction.ConfigureAwait(true);
                if (mutateInsideTransaction)
                {
                    command.Transaction = transaction;
                    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
                }
                await transaction.CommitAsync(ct).ConfigureAwait(true);
            }

            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(0, pool.IdleCount);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedManagedCompletion_RetiresEvenAfterSuccessfulDisposeRollback(bool commit)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
            Assert.Equal(
                commit ? "COMMIT WORK" : "ROLLBACK WORK",
                await FakeStarRocksServer.ReadCommandAndReplyErrorAsync(stream).ConfigureAwait(true)
            );
            Assert.Equal(
                "ROLLBACK WORK",
                await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true)
            );
            Assert.Equal(0, await stream.ReadAsync(new byte[1], ct).ConfigureAwait(true));
            closed.TrySetResult();
        });
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            using (var connection = new DotRocksConnection(connectionString))
            {
                await connection.OpenAsync(ct).ConfigureAwait(true);
                DbTransaction transaction = await connection
                    .BeginTransactionAsync(ct)
                    .ConfigureAwait(true);
                await Assert
                    .ThrowsAsync<DotRocksException>(() =>
                        commit ? transaction.CommitAsync(ct) : transaction.RollbackAsync(ct)
                    )
                    .ConfigureAwait(true);
                await transaction.DisposeAsync().ConfigureAwait(true);
            }

            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(0, pool.IdleCount);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnresolvedManagedTransaction_RetiresPhysicalConnection(bool disposeFails)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
            if (disposeFails)
            {
                Assert.Equal(
                    "ROLLBACK WORK",
                    await FakeStarRocksServer
                        .ReadCommandAndReplyErrorAsync(stream)
                        .ConfigureAwait(true)
                );
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], ct).ConfigureAwait(true));
            closed.TrySetResult();
        });
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            using var connection = new DotRocksConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(true);
            DbTransaction transaction = await connection
                .BeginTransactionAsync(ct)
                .ConfigureAwait(true);
            if (disposeFails)
            {
                await transaction.DisposeAsync().ConfigureAwait(true);
            }
            else
            {
                await connection.CloseAsync().ConfigureAwait(true);
                await transaction.DisposeAsync().ConfigureAwait(true);
            }
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(0, pool.IdleCount);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
        }
    }

    [Fact]
    public async Task CloseDuringManagedBegin_DiscardsTheInFlightTransaction()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var startReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            var reader = new PacketReader(stream);
            reader.ResetSequence(0);
            _ = await reader.ReadPayloadAsync(ct).ConfigureAwait(true);
            startReceived.TrySetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], ct).ConfigureAwait(true));
            closed.TrySetResult();
        });
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            using var connection = new DotRocksConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(true);
            Task<DbTransaction> begin = connection.BeginTransactionAsync(ct).AsTask();
            await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
            await connection.CloseAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<DotRocksException>(() => begin).ConfigureAwait(true);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(0, pool.IdleCount);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
        }
    }

    [Fact]
    public async Task FailedBegin_DetachesTransactionButRetainsConservativeRetirement()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer
                .ReadCommandAndReplyErrorAsync(stream)
                .ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], ct).ConfigureAwait(true));
            closed.TrySetResult();
        });
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            using (var connection = new DotRocksConnection(connectionString))
            {
                await connection.OpenAsync(ct).ConfigureAwait(true);
                await Assert
                    .ThrowsAsync<DotRocksException>(() =>
                        connection.BeginTransactionAsync(ct).AsTask()
                    )
                    .ConfigureAwait(true);
                DbTransaction transaction = await connection
                    .BeginTransactionAsync(ct)
                    .ConfigureAwait(true);
                await using (transaction.ConfigureAwait(true))
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(true);
                }
            }
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(0, pool.IdleCount);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
        }
    }

    private static async Task<string?> ReadConnectionIdAsync(
        DotRocksConnection connection,
        CancellationToken cancellationToken
    )
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CONNECTION_ID()";
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(true);
    }
}
