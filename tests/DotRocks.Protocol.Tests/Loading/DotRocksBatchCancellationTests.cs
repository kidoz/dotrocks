using System.Data;
using System.Data.Common;
using DotRocks.Data;
using DotRocks.Data.Pooling;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class DotRocksBatchCancellationTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("token")]
    [InlineData("abort")]
    public async Task AbortDuringRead_PreservesCancellationAndReleasesPoolLease(string mode)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = FakeStarRocksServer.Start(
            async stream =>
            {
                await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
                var reader = new PacketReader(stream);
                reader.ResetSequence(0);
                _ = await reader.ReadPayloadAsync(ct).ConfigureAwait(true);
                received.SetResult();
                Assert.Equal(0, await stream.ReadAsync(new byte[1], ct).ConfigureAwait(true));
            },
            async stream =>
            {
                await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
                _ = await FakeStarRocksServer
                    .ReadCommandAndReplyOkAsync(stream)
                    .ConfigureAwait(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(true);
            }
        );
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            using var connection = new DotRocksConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(true);
            using DbBatch batch = connection.CreateBatch();
            batch.Timeout = 0;
            DbBatchCommand command = batch.CreateBatchCommand();
            command.CommandText = "SELECT value FROM stalled";
            batch.BatchCommands.Add(command);
            using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<int> execution = batch.ExecuteNonQueryAsync(callerCancellation.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);

            if (mode == "token")
            {
                // Cancellation callbacks run in reverse registration order. Abort and await the
                // failed read before cancellation reaches the linked I/O token, forcing the
                // transport-exception path rather than relying on a socket cancellation race.
                using CancellationTokenRegistration registration =
                    callerCancellation.Token.Register(() =>
                        AbortAndWaitForRead(connection, execution)
                    );
                await callerCancellation.CancelAsync().ConfigureAwait(true);
            }
            else if (mode == "cancel")
            {
                batch.Cancel();
            }
            else
            {
                connection.Abort();
            }

            if (mode == "abort")
            {
                await Assert.ThrowsAsync<DotRocksException>(() => execution).ConfigureAwait(true);
            }
            else
            {
                OperationCanceledException exception = await Assert
                    .ThrowsAnyAsync<OperationCanceledException>(() => execution)
                    .ConfigureAwait(true);
                Assert.True(exception.CancellationToken.IsCancellationRequested);
                if (mode == "token")
                {
                    Assert.Equal(callerCancellation.Token, exception.CancellationToken);
                    Assert.IsType<DotRocksException>(exception.InnerException);
                }
            }
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(0, pool.IdleCount);

            using var next = new DotRocksConnection(connectionString);
            await next.OpenAsync(ct).ConfigureAwait(true);
            batch.Connection = next;
            Assert.Equal(0, await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(true));
            Assert.Equal(2, server.ConnectionCount);
        }
        finally
        {
            release.TrySetResult();
            DotRocksConnectionPool.Clear(options);
        }
    }

    private static void AbortAndWaitForRead(DotRocksConnection connection, Task execution)
    {
        connection.Abort();
        try
        {
            execution.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
            when (exception is DotRocksException or OperationCanceledException)
        {
            // The test observes the result outside the callback, after cancellation completes.
        }
    }
}
