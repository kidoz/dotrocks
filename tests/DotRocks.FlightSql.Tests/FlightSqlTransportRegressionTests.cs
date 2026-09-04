using Apache.Arrow;
using DotRocks.Data;
using DotRocks.FlightSql;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DotRocks.FlightSql.Tests;

public sealed partial class FlightSqlTransportTests
{
    private const string SensitiveValue = "synthetic-private-value";

    [Theory]
    [InlineData("handshake")]
    [InlineData("query")]
    [InlineData("update")]
    [InlineData("begin")]
    [InlineData("end")]
    [InlineData("first-batch")]
    [InlineData("later-batch")]
    public async Task RemoteErrors_RetainStatusWithoutSensitiveDetails(string phase)
    {
        using IHost host = CreateHost();
        CancellationToken token = TestContext.Current.CancellationToken;
        await host.StartAsync(token).ConfigureAwait(true);
        try
        {
            QueryCapture capture = GetQueryCapture(host);
            capture.FailurePhase = phase;
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var source = new DotRocksFlightSqlDataSource(options);

            RpcException exception = await Assert
                .ThrowsAsync<RpcException>(async () =>
                {
                    if (phase == "update")
                    {
                        _ = await source
                            .ExecuteUpdateAsync("INSERT INTO t VALUES (1)", token)
                            .ConfigureAwait(true);
                    }
                    else if (phase == "begin")
                    {
                        _ = await source.BeginTransactionAsync(token).ConfigureAwait(true);
                    }
                    else if (phase == "end")
                    {
                        await using var transaction = await source
                            .BeginTransactionAsync(token)
                            .ConfigureAwait(true);
                        try
                        {
                            await transaction.CommitAsync(token).ConfigureAwait(true);
                        }
                        finally
                        {
                            capture.FailurePhase = null; // Allow disposal to roll the transaction back.
                        }
                    }
                    else if (phase is "first-batch" or "later-batch")
                    {
                        DotRocksFlightSqlResult result = await source
                            .ExecuteQueryAsync("SELECT value FROM t", token)
                            .ConfigureAwait(true);
                        await foreach (
                            RecordBatch batch in result
                                .ReadRecordBatchesAsync(token)
                                .ConfigureAwait(true)
                        )
                        {
                            batch.Dispose();
                        }
                    }
                    else
                    {
                        await using var connection = source.CreateConnection();
                        await connection.OpenAsync(token).ConfigureAwait(true);
                        await using var command = connection.CreateCommand();
                        command.CommandText = "SELECT @value";
                        command.Parameters.Add(
                            new DotRocksParameter
                            {
                                ParameterName = "value",
                                Value = SensitiveValue,
                            }
                        );
                        await using var reader = await command
                            .ExecuteReaderAsync(token)
                            .ConfigureAwait(true);
                    }
                })
                .ConfigureAwait(true);

            Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
            Assert.DoesNotContain(SensitiveValue, exception.ToString(), StringComparison.Ordinal);
            Assert.Empty(exception.Trailers);
            Assert.Null(exception.InnerException);
            Assert.Null(exception.Status.DebugException);
        }
        finally
        {
            await host.StopAsync(token).ConfigureAwait(true);
        }
    }
}
