using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using DotRocks.Data;
using DotRocks.Data.Pooling;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.Pooling;

public sealed class NativeSessionRegressionTests
{
    [Theory]
    [InlineData("START TRANSACTION", false)]
    [InlineData("BEGIN", true)]
    [InlineData("/* transaction */ START TRANSACTION", true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Theory inputs are fixed transaction-control SQL statements."
    )]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA1849:Call async methods when in an async method",
        Justification = "The theory deliberately verifies synchronous and asynchronous ADO.NET execution paths."
    )]
    public async Task RawTransactionSql_RetiresPhysicalSessionBeforeAnotherLease(
        string sql,
        bool asynchronous
    )
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var firstClosed = new TaskCompletionSource();
        using var server = FakeStarRocksServer.Start(
            async stream =>
            {
                await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
                Assert.Equal(
                    sql,
                    await FakeStarRocksServer
                        .ReadCommandAndReplyOkAsync(stream)
                        .ConfigureAwait(true)
                );
                Assert.Equal(0, await stream.ReadAsync(new byte[1], ct).ConfigureAwait(true));
                firstClosed.TrySetResult();
            },
            async stream =>
            {
                await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
                _ = await FakeStarRocksServer
                    .ReadCommandAndReplyOkAsync(stream)
                    .ConfigureAwait(true);
            }
        );
        string connectionString = server.ConnectionString + ";Pooling=true;Maximum Pool Size=1";
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            using (var first = new DotRocksConnection(connectionString))
            {
                await first.OpenAsync(ct).ConfigureAwait(true);
                using DbCommand command = first.CreateCommand();
                command.CommandText = sql;
                _ = asynchronous
                    ? await command.ExecuteNonQueryAsync(ct).ConfigureAwait(true)
                    : command.ExecuteNonQuery();
            }

            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(0, pool.IdleCount);
            await firstClosed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
            using var next = new DotRocksConnection(connectionString);
            await next.OpenAsync(ct).ConfigureAwait(true);
            using DbCommand nextCommand = next.CreateCommand();
            nextCommand.CommandText = "SELECT 1";
            Assert.Equal(0, await nextCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(true));
            Assert.Equal(2, server.ConnectionCount);
        }
        finally
        {
            DotRocksConnectionPool.Clear(options);
        }
    }

}
