using System.Diagnostics.CodeAnalysis;
using Apache.Arrow;
using DotRocks.Data;
using DotRocks.FlightSql;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DotRocks.FlightSql.Tests;

public sealed partial class FlightSqlTransportTests
{
    private const string SensitiveValue = "synthetic-private-value";

    [Fact]
    public async Task MigrationHistory_ClonesCredentialedFlightConnection()
    {
        using IHost host = CreateHost();
        CancellationToken token = TestContext.Current.CancellationToken;
        await host.StartAsync(token).ConfigureAwait(true);
        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var connection = new DotRocksFlightSqlDbConnection(
                options,
                "Server=localhost;Database=history_database;Password=fallback-secret",
                DotRocksFlightSqlFallbackMode.WriteCommands
            );
            await connection.OpenAsync(token).ConfigureAwait(true);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM test";
            Assert.Equal(1, await command.ExecuteScalarAsync(token).ConfigureAwait(true));
            var contextOptions = new DbContextOptionsBuilder<FlightContext>()
                .UseStarRocks(connection)
                .Options;
            await using var context = new FlightContext(contextOptions);
            IHistoryRepository history = context.GetService<IHistoryRepository>();
            Assert.True(await history.ExistsAsync(token).ConfigureAwait(true));
            Assert.Contains(
                "information_schema.tables",
                GetQueryCapture(host).LastQuery,
                StringComparison.Ordinal
            );
            Assert.Contains(
                "DATABASE()",
                GetQueryCapture(host).LastQuery,
                StringComparison.Ordinal
            );
            Assert.Equal(2, GetQueryCapture(host).Handshakes);
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
            Assert.Equal(1, await command.ExecuteScalarAsync(token).ConfigureAwait(true));
        }
        finally
        {
            await host.StopAsync(token).ConfigureAwait(true);
        }
    }

    [Theory]
    [InlineData(DotRocksFlightSqlFallbackMode.None)]
    [InlineData(DotRocksFlightSqlFallbackMode.WriteCommands)]
    public async Task NonQuery_ReadsUseFlightQueryPath(DotRocksFlightSqlFallbackMode fallbackMode)
    {
        using IHost host = CreateHost();
        CancellationToken token = TestContext.Current.CancellationToken;
        await host.StartAsync(token).ConfigureAwait(true);
        try
        {
            await using var connection =
                fallbackMode == DotRocksFlightSqlFallbackMode.None
                    ? new DotRocksFlightSqlDbConnection(
                        new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
                        {
                            AllowInsecureTransport = true,
                        }
                    )
                    : CreateFallbackConnection(host, fallbackMode);
            await connection.OpenAsync(token).ConfigureAwait(true);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM test";
            Assert.Equal(-1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(true));
            Assert.Equal("SELECT value FROM test", GetQueryCapture(host).LastQuery);
            Assert.Empty(GetQueryCapture(host).LastUpdate);
        }
        finally
        {
            await host.StopAsync(token).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Transaction_PreCanceledCompletionRemainsActive()
    {
        using IHost host = CreateHost();
        CancellationToken token = TestContext.Current.CancellationToken;
        await host.StartAsync(token).ConfigureAwait(true);
        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var source = new DotRocksFlightSqlDataSource(options);
            await using var transaction = await source
                .BeginTransactionAsync(token)
                .ConfigureAwait(true);
            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync().ConfigureAwait(true);
            await Assert
                .ThrowsAnyAsync<OperationCanceledException>(() =>
                    transaction.CommitAsync(canceled.Token)
                )
                .ConfigureAwait(true);
            Assert.False(transaction.IsInDoubt);
            Assert.False(transaction.IsCompleted);
            Assert.Equal(0, GetQueryCapture(host).EndTransactions);
            await transaction.RollbackAsync(token).ConfigureAwait(true);
            Assert.True(transaction.IsCompleted);
        }
        finally
        {
            await host.StopAsync(token).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Clone_PreservesCredentialsWithIndependentFlightSession()
    {
        using IHost host = CreateHost();
        CancellationToken token = TestContext.Current.CancellationToken;
        await host.StartAsync(token).ConfigureAwait(true);
        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var source = new DotRocksFlightSqlDbConnection(
                options,
                "Server=localhost;Database=clone_database;Password=fallback-secret",
                DotRocksFlightSqlFallbackMode.WriteCommands
            );
            await source.OpenAsync(token).ConfigureAwait(true);
            await using var command = source.CreateCommand();
            command.CommandText = "SELECT value FROM test";
            Assert.Equal(1, await command.ExecuteScalarAsync(token).ConfigureAwait(true));
            await using var clone = (DotRocksFlightSqlDbConnection)((ICloneable)source).Clone();
            Assert.Equal(System.Data.ConnectionState.Closed, clone.State);
            Assert.Equal(source.FallbackMode, clone.FallbackMode);
            Assert.Equal(source.Database, clone.Database);
            Assert.DoesNotContain("secret", clone.ConnectionString, StringComparison.Ordinal);
            await clone.OpenAsync(token).ConfigureAwait(true);
            await using var cloneCommand = clone.CreateCommand();
            cloneCommand.CommandText = "SELECT value FROM test";
            Assert.Equal(1, await cloneCommand.ExecuteScalarAsync(token).ConfigureAwait(true));
            Assert.Equal(2, GetQueryCapture(host).Handshakes);
            await clone.DisposeAsync().ConfigureAwait(true);
            Assert.Equal(1, await command.ExecuteScalarAsync(token).ConfigureAwait(true));
        }
        finally
        {
            await host.StopAsync(token).ConfigureAwait(true);
        }
    }

    [Theory]
    [InlineData("INSERT INTO unavailable_discovery VALUES (1)")]
    [InlineData("UPDATE unavailable_discovery SET value = 1")]
    [InlineData("SELECT 1; INSERT INTO unavailable_discovery VALUES (1)")]
    [InlineData("SELECT 1 INTO OUTFILE 'unavailable_discovery'")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "SQL is supplied exclusively by fixed InlineData cases."
    )]
    public async Task ReadFallback_DoesNotReplayWritesAfterDiscoveryFailure(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        using IHost host = CreateHost();
        CancellationToken token = TestContext.Current.CancellationToken;
        await host.StartAsync(token).ConfigureAwait(true);
        try
        {
            await using var connection = CreateFallbackConnection(
                host,
                DotRocksFlightSqlFallbackMode.ReadQueries
            );
            await connection.OpenAsync(token).ConfigureAwait(true);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            // A replay would attempt the closed MySQL port and replace this RPC error with a
            // DotRocks connection error. An INSERT has already committed in the scripted server.
            RpcException exception = await Assert
                .ThrowsAsync<RpcException>(() => command.ExecuteReaderAsync(token))
                .ConfigureAwait(true);

            Assert.Equal(StatusCode.Unavailable, exception.StatusCode);
            Assert.Equal(sql, GetQueryCapture(host).LastQuery);
            Assert.Equal(
                sql.StartsWith("INSERT", StringComparison.Ordinal) ? 1 : 0,
                GetQueryCapture(host).CommittedWrites
            );
        }
        finally
        {
            await host.StopAsync(token).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task WriteFallback_RoutesReaderWritesBeforeContactingFlight()
    {
        using IHost host = CreateHost();
        CancellationToken token = TestContext.Current.CancellationToken;
        await host.StartAsync(token).ConfigureAwait(true);
        try
        {
            await using var connection = CreateFallbackConnection(
                host,
                DotRocksFlightSqlFallbackMode.WriteCommands
            );
            await connection.OpenAsync(token).ConfigureAwait(true);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO unavailable_discovery VALUES (1)";

            for (int attempt = 0; attempt < 2; attempt++)
            {
                // Repeat to verify that a failed pre-routed execution releases its operation gate.
                await Assert
                    .ThrowsAsync<DotRocksException>(() => command.ExecuteReaderAsync(token))
                    .ConfigureAwait(true);
            }

            Assert.Empty(GetQueryCapture(host).LastQuery);
            Assert.Equal(0, GetQueryCapture(host).Handshakes);
            Assert.Equal(0, GetQueryCapture(host).CommittedWrites);
        }
        finally
        {
            await host.StopAsync(token).ConfigureAwait(true);
        }
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Both conditional command texts are fixed test literals."
    )]
    public async Task ExecuteScalarAsync_DistinguishesNullFromNoRows(bool hasRow)
    {
        using IHost host = CreateHost();
        CancellationToken token = TestContext.Current.CancellationToken;
        await host.StartAsync(token).ConfigureAwait(true);
        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var connection = new DotRocksFlightSqlDbConnection(options);
            await connection.OpenAsync(token).ConfigureAwait(true);
            await using var command = connection.CreateCommand();
            command.CommandText = hasRow ? "SELECT null_value" : "SELECT value FROM empty";

            object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(true);

            if (hasRow)
            {
                Assert.Same(DBNull.Value, value);
            }
            else
            {
                Assert.Null(value);
            }
        }
        finally
        {
            await host.StopAsync(token).ConfigureAwait(true);
        }
    }

    private static DotRocksFlightSqlDbConnection CreateFallbackConnection(
        IHost host,
        DotRocksFlightSqlFallbackMode mode
    ) =>
        new(
            new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            },
            "Server=127.0.0.1;Port=1;User ID=root;Connection Timeout=1",
            mode
        );
}
