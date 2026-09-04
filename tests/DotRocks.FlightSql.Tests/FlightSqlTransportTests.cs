using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Types;
using Arrow.Flight.Protocol.Sql;
using DotRocks.Data;
using DotRocks.FlightSql;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace DotRocks.FlightSql.Tests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "Await-using declarations in xUnit tests intentionally retain the test context."
)]
public sealed partial class FlightSqlTransportTests
{
    [Fact]
    public async Task ExecuteQueryAsync_StreamsRecordBatchesWithAuthorization()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            Uri address = GetServerAddress(host);
            var options = new DotRocksFlightSqlOptions(address, "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            using var dataSource = new DotRocksFlightSqlDataSource(options);

            DotRocksFlightSqlResult result = await dataSource
                .ExecuteQueryAsync("SELECT value FROM test", cancellationToken)
                .ConfigureAwait(true);

            Assert.NotNull(result.Schema);
            Assert.Equal(2, result.TotalRecords);
            Assert.Single(result.Schema.FieldsList);

            var values = new List<int>();
            await foreach (
                RecordBatch batch in result
                    .ReadRecordBatchesAsync(cancellationToken)
                    .ConfigureAwait(true)
            )
            {
                using (batch)
                {
                    var array = Assert.IsType<Int32Array>(batch.Column(0));
                    values.AddRange(
                        Enumerable
                            .Range(0, array.Length)
                            .Select(index => array.GetValue(index)!.Value)
                    );
                }
            }

            Assert.Equal([1, 2], values);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ExecuteQueryAsync_SkipsUnsupportedEndpointLocationForTrustedAlternative()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            Uri address = GetServerAddress(host);
            GetQueryCapture(host).SelfAddress = address.AbsoluteUri;
            var options = new DotRocksFlightSqlOptions(address, "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            using var dataSource = new DotRocksFlightSqlDataSource(options);

            DotRocksFlightSqlResult result = await dataSource
                .ExecuteQueryAsync("SELECT value FROM multi_location", cancellationToken)
                .ConfigureAwait(true);

            // The unusable first location must be skipped, not fail the whole read.
            var values = new List<int>();
            await foreach (
                RecordBatch batch in result
                    .ReadRecordBatchesAsync(cancellationToken)
                    .ConfigureAwait(true)
            )
            {
                using (batch)
                {
                    var array = Assert.IsType<Int32Array>(batch.Column(0));
                    values.AddRange(
                        Enumerable
                            .Range(0, array.Length)
                            .Select(index => array.GetValue(index)!.Value)
                    );
                }
            }

            Assert.Equal([1, 2], values);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DbCommand_FallsBackToMySqlOnlyWhenStatementDiscoveryFails()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            Uri address = GetServerAddress(host);
            var options = new DotRocksFlightSqlOptions(address, "root", "secret")
            {
                AllowInsecureTransport = true,
            };

            // The fallback target is a closed port, so an attempted fallback fails with the
            // driver's connect error — distinguishable from the Flight failure it would replace.
            await using var connection = new DotRocksFlightSqlDbConnection(
                options,
                "Server=127.0.0.1;Port=1;User ID=root;Connection Timeout=1",
                DotRocksFlightSqlFallbackMode.ReadQueries
            );
            await connection.OpenAsync(cancellationToken).ConfigureAwait(true);

            // Only the read-only SELECT is eligible for retry after an ambiguous discovery
            // failure. A write could already have executed before this failure arrived.
            await using DotRocksFlightSqlCommand discovery = connection.CreateCommand();
            discovery.CommandText = "SELECT value FROM unavailable_discovery";
            DotRocksException fallbackFailure = await Assert
                .ThrowsAsync<DotRocksException>(() =>
                    discovery.ExecuteReaderAsync(cancellationToken)
                )
                .ConfigureAwait(true);
            Assert.Contains("connect", fallbackFailure.Message, StringComparison.OrdinalIgnoreCase);

            // A failure fetching the result (DoGet) comes after the statement ran; re-running the
            // text over MySQL would execute it twice, so the Flight error must surface unchanged.
            await using DotRocksFlightSqlCommand fetch = connection.CreateCommand();
            fetch.CommandText = "SELECT value FROM unavailable_fetch";
            Exception fetchFailure = await Assert
                .ThrowsAnyAsync<Exception>(() => fetch.ExecuteReaderAsync(cancellationToken))
                .ConfigureAwait(true);
            RpcException rpc = Assert.IsType<RpcException>(FindInChain<RpcException>(fetchFailure));
            Assert.Equal(StatusCode.Unavailable, rpc.StatusCode);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private static TException? FindInChain<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }

    [Fact]
    public async Task DbCommand_BindsParametersAndMaterializesRows()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var connection = new DotRocksFlightSqlDbConnection(options);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
            await using DotRocksFlightSqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT @value AS value";
            command.Parameters.Add(
                new DotRocks.Data.DotRocksParameter { ParameterName = "value", Value = 7 }
            );

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(true);

            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("value", reader.GetName(0));
            Assert.Equal("SELECT 7 AS value", GetQueryCapture(host).LastQuery);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DbCommand_CancelReportsOperationCanceledAndAllowsReuse()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var connection = new DotRocksFlightSqlDbConnection(options);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
            await using DotRocksFlightSqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT blocking";
            Task<DbDataReader> pendingExecute = command.ExecuteReaderAsync(CancellationToken.None);
            await GetQueryCapture(host)
                .DoGetStarted.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(true);

            command.Cancel();

            // ADO.NET consumers must be able to detect cancellation without inspecting gRPC status
            // codes, so the channel is configured to translate it.
            await Assert
                .ThrowsAnyAsync<OperationCanceledException>(() => pendingExecute)
                .ConfigureAwait(true);

            command.CommandText = "SELECT value FROM test";
            await using DbDataReader secondReader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(true);
            Assert.True(await secondReader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(1, secondReader.GetInt32(0));
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DataSource_AuthenticatesOnceAndReusesTheServerSession()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            var dataSource = new DotRocksFlightSqlDataSource(options);
            await using (dataSource.ConfigureAwait(true))
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await using DotRocksFlightSqlDbConnection connection =
                        dataSource.CreateConnection();
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
                    await using DotRocksFlightSqlCommand command = connection.CreateCommand();
                    command.CommandText = "SELECT value FROM test";
                    await using DbDataReader reader = await command
                        .ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(true);
                    Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
                }
            }

            QueryCapture capture = GetQueryCapture(host);
            Assert.Equal(1, capture.Handshakes);

            // Every discovery and DoGet call must reuse the session instead of re-authenticating,
            // which is what made StarRocks leak one frontend connection per RPC.
            Assert.Equal(0, capture.BasicAuthorizedCalls);

            // Three discovery calls, three DoGet calls, and the CloseSession action on disposal.
            Assert.Equal(7, capture.SessionAuthorizedCalls);
            Assert.Equal(1, capture.ClosedSessions);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DataSource_FallsBackToPerCallCredentialsWithoutHandshakeSupport()
    {
        using IHost host = CreateHost<BasicOnlyFlightServer>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            using var dataSource = new DotRocksFlightSqlDataSource(options);

            DotRocksFlightSqlResult result = await dataSource
                .ExecuteQueryAsync("SELECT value FROM test", cancellationToken)
                .ConfigureAwait(true);
            await foreach (
                RecordBatch batch in result
                    .ReadRecordBatchesAsync(cancellationToken)
                    .ConfigureAwait(true)
            )
            {
                batch.Dispose();
            }

            QueryCapture capture = GetQueryCapture(host);
            Assert.Equal(0, capture.Handshakes);
            Assert.Equal(2, capture.BasicAuthorizedCalls);
            Assert.Equal(0, capture.SessionAuthorizedCalls);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DbDataReader_ReportsNoRowsWhenTheServerDeclaresNoRecordCount()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var connection = new DotRocksFlightSqlDbConnection(options);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
            await using DotRocksFlightSqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM empty";

            await using DbDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(true);

            Assert.False(reader.HasRows);
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DbDataReader_ReportsRowsBeforeTheFirstRead()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var connection = new DotRocksFlightSqlDbConnection(options);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
            await using DotRocksFlightSqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM test";

            await using DbDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(true);

            Assert.True(reader.HasRows);
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(1, reader.GetInt32(0));
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(2, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task EntityFrameworkCore_ExecutesAsyncQueryThroughFlightConnection()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var flightOptions = new DotRocksFlightSqlOptions(
                GetServerAddress(host),
                "root",
                "secret"
            )
            {
                AllowInsecureTransport = true,
            };
            await using var connection = new DotRocksFlightSqlDbConnection(flightOptions);
            var optionsBuilder = new DbContextOptionsBuilder<FlightContext>();
            optionsBuilder.UseStarRocks(connection);
            await using var context = new FlightContext(optionsBuilder.Options);

            List<Widget> widgets = await context
                .Widgets.OrderBy(widget => widget.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(true);

            Assert.Collection(
                widgets,
                widget =>
                {
                    Assert.Equal(1, widget.Id);
                    Assert.Equal("alpha", widget.Name);
                },
                widget =>
                {
                    Assert.Equal(2, widget.Id);
                    Assert.Equal("beta", widget.Name);
                }
            );
            Assert.Contains(
                "FROM `widgets`",
                GetQueryCapture(host).LastQuery,
                StringComparison.Ordinal
            );
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_UsesStatementUpdateDoPut()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var connection = new DotRocksFlightSqlDbConnection(options);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
            await using DotRocksFlightSqlCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE widgets SET name = @name WHERE id = @id";
            command.Parameters.Add(
                new DotRocks.Data.DotRocksParameter { ParameterName = "name", Value = "updated" }
            );
            command.Parameters.Add(
                new DotRocks.Data.DotRocksParameter { ParameterName = "id", Value = 1 }
            );

            int affectedRows = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(3, affectedRows);
            Assert.Equal(
                "UPDATE widgets SET name = 'updated' WHERE id = 1",
                GetQueryCapture(host).LastUpdate
            );
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Transaction_ForwardsHandleToUpdatesAndCommits()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            using var dataSource = new DotRocksFlightSqlDataSource(options);
            await using DotRocksFlightSqlTransaction transaction = await dataSource
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(true);

            long affectedRows = await transaction
                .ExecuteUpdateAsync("DELETE FROM widgets WHERE id = 1", cancellationToken)
                .ConfigureAwait(true);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);

            QueryCapture capture = GetQueryCapture(host);
            Assert.Equal(3, affectedRows);
            Assert.Equal("transaction-1", capture.LastUpdateTransactionId);
            Assert.Equal("EndTransaction", capture.LastAction);
            Assert.Equal(1, capture.LastEndTransactionAction);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Transaction_StaysActiveWhenTheServerRejectsCompletion()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            using var dataSource = new DotRocksFlightSqlDataSource(options);
            await using DotRocksFlightSqlTransaction transaction = await dataSource
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(true);
            QueryCapture capture = GetQueryCapture(host);
            capture.FailEndTransaction = true;

            await Assert
                .ThrowsAsync<RpcException>(() => transaction.CommitAsync(cancellationToken))
                .ConfigureAwait(true);

            // A commit the server never confirmed must leave the transaction recoverable.
            Assert.False(transaction.IsCompleted);
            capture.FailEndTransaction = false;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);
            Assert.True(transaction.IsCompleted);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DbTransaction_FailedCompletionLeavesTheConnectionUsable()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var connection = new DotRocksFlightSqlDbConnection(options);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
            QueryCapture capture = GetQueryCapture(host);

            DbTransaction failedCommit = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(true);
            capture.FailEndTransaction = true;
            await Assert
                .ThrowsAsync<RpcException>(() => failedCommit.CommitAsync(cancellationToken))
                .ConfigureAwait(true);
            capture.FailEndTransaction = false;
            await failedCommit.CommitAsync(cancellationToken).ConfigureAwait(true);
            await failedCommit.DisposeAsync().ConfigureAwait(true);

            DbTransaction failedRollback = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(true);
            capture.FailEndTransaction = true;
            await Assert
                .ThrowsAsync<RpcException>(async () =>
                    await failedRollback.DisposeAsync().ConfigureAwait(true)
                )
                .ConfigureAwait(true);
            capture.FailEndTransaction = false;

            // A rollback the server rejected must not leave the connection owning a dead handle.
            await using DbTransaction recovered = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(true);
            Assert.NotNull(recovered);
            await recovered.RollbackAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ConnectionCloseAsync_RollsBackActiveTransactionAsynchronously()
    {
        using IHost host = CreateHost();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            var options = new DotRocksFlightSqlOptions(GetServerAddress(host), "root", "secret")
            {
                AllowInsecureTransport = true,
            };
            await using var connection = new DotRocksFlightSqlDbConnection(options);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
            await using DbTransaction transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(true);

            await connection.CloseAsync().ConfigureAwait(true);

            Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
            Assert.Equal("EndTransaction", GetQueryCapture(host).LastAction);
            Assert.Equal(2, GetQueryCapture(host).LastEndTransactionAction);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private static IHost CreateHost() => CreateHost<AuthorizedFlightServer>();

    private static IHost CreateHost<TServer>()
        where TServer : FlightServer =>
        Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(options =>
                {
                    options.Listen(
                        IPAddress.Loopback,
                        0,
                        listenOptions => listenOptions.Protocols = HttpProtocols.Http2
                    );
                });
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton<QueryCapture>();
                    services.AddGrpc().AddFlightServer<TServer>();
                });
                webBuilder.Configure(application =>
                {
                    application.UseRouting();
                    application.UseEndpoints(endpoints => endpoints.MapFlightEndpoint());
                });
            })
            .Build();

    private static Uri GetServerAddress(IHost host)
    {
        IServer server = host.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses =
            server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("The in-process Flight server has no address.");
        return new Uri(Assert.Single(addresses.Addresses));
    }

    private static QueryCapture GetQueryCapture(IHost host) =>
        host.Services.GetRequiredService<QueryCapture>();

    private class AuthorizedFlightServer : FlightServer
    {
        private const string ExpectedBasicAuthorization = "Basic cm9vdDpzZWNyZXQ=";
        private const string SessionAuthorization = "Bearer test-session";
        private static readonly Schema ResultSchema = new(
            [new Field("value", Int32Type.Default, false)],
            null
        );
        private static readonly Schema WidgetSchema = new(
            [
                new Field("Id", Int32Type.Default, false),
                new Field("Name", StringType.Default, false),
            ],
            null
        );
        private readonly QueryCapture _capture;

        public AuthorizedFlightServer(QueryCapture capture)
        {
            _capture = capture;
        }

        /// <summary>
        /// Mirrors StarRocks: the credentials are exchanged once for a session bearer token.
        /// </summary>
        public override async Task Handshake(
            IAsyncStreamReader<FlightHandshakeRequest> requestStream,
            IAsyncStreamWriter<FlightHandshakeResponse> responseStream,
            ServerCallContext context
        )
        {
            ThrowIfSensitiveFailure("handshake");
            string? authorization = ReadAuthorization(context);
            if (authorization != ExpectedBasicAuthorization)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Unauthorized."));
            }

            Interlocked.Increment(ref _capture.Handshakes);
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                // The handshake payload is unused; only the returned token matters.
            }

            await context
                .WriteResponseHeadersAsync(
                    new Metadata { { "authorization", SessionAuthorization } }
                )
                .ConfigureAwait(false);
        }

        public override Task<FlightInfo> GetFlightInfo(
            FlightDescriptor request,
            ServerCallContext context
        )
        {
            RequireAuthorization(context);
            string query = FlightSqlServer.GetCommand(request) is CommandStatementQuery command
                ? command.Query
                : string.Empty;
            _capture.LastQuery = query;
            ThrowIfSensitiveFailure("query");
            if (query.Contains("unavailable_discovery", StringComparison.OrdinalIgnoreCase))
            {
                // Simulate a committed write whose response is lost. Reader execution does not
                // imply read-only SQL, so the client must not replay it over the fallback.
                if (query.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _capture.CommittedWrites);
                }
                throw new RpcException(new Status(StatusCode.Unavailable, "Frontend unavailable."));
            }

            bool widgets = query.Contains("widgets", StringComparison.OrdinalIgnoreCase);
            bool blocking = query.Contains("blocking", StringComparison.OrdinalIgnoreCase);
            bool empty = query.Contains("empty", StringComparison.OrdinalIgnoreCase);
            bool unavailableFetch = query.Contains(
                "unavailable_fetch",
                StringComparison.OrdinalIgnoreCase
            );
            string ticket =
                widgets ? "widgets"
                : blocking ? "blocking"
                : empty ? "empty"
                : unavailableFetch ? "unavailable"
                : "result";
            Schema schema = widgets ? WidgetSchema : ResultSchema;

            // A multi-location endpoint advertises an address DotRocks cannot use (a Unix socket)
            // ahead of this server's own trusted address, as a StarRocks FE with mixed backends
            // might.
            FlightLocation[] locations = query.Contains(
                "multi_location",
                StringComparison.OrdinalIgnoreCase
            )
                ?
                [
                    new FlightLocation("grpc+unix:///tmp/backend.sock"),
                    new FlightLocation(_capture.SelfAddress),
                ]
                : [];
            var endpoint = new FlightEndpoint(new FlightTicket(ticket), locations);

            // StarRocks does not declare a record count, so -1 stands in for "unknown".
            return Task.FromResult(new FlightInfo(schema, request, [endpoint], empty ? -1 : 2, -1));
        }

        public override async Task DoGet(
            FlightTicket ticket,
            FlightServerRecordBatchStreamWriter responseStream,
            ServerCallContext context
        )
        {
            RequireAuthorization(context);
            ThrowIfSensitiveFailure("first-batch");
            string ticketValue = ticket.Ticket.ToStringUtf8();
            if (ticketValue == "unavailable")
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Backend unavailable."));
            }

            if (ticketValue == "blocking")
            {
                _capture.DoGetStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (ticketValue == "empty")
            {
                // An empty StarRocks result still carries the schema, as a zero-row batch.
                using var noValues = new Int32Array.Builder().Build();
                using var emptyBatch = new RecordBatch(ResultSchema, [noValues], 0);
                await responseStream.WriteAsync(emptyBatch).ConfigureAwait(false);
                return;
            }

            if (ticketValue == "widgets")
            {
                using var ids = new Int32Array.Builder().Append(1).Append(2).Build();
                using var names = new StringArray.Builder().Append("alpha").Append("beta").Build();
                using var widgetBatch = new RecordBatch(WidgetSchema, [ids, names], 2);
                await responseStream.WriteAsync(widgetBatch).ConfigureAwait(false);
                return;
            }

            if (ticketValue != "result")
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown ticket."));
            }

            using var values = new Int32Array.Builder().Append(1).Append(2).Build();
            using var batch = new RecordBatch(ResultSchema, [values], 2);
            await responseStream.WriteAsync(batch).ConfigureAwait(false);
            ThrowIfSensitiveFailure("later-batch");
        }

        public override async Task DoPut(
            FlightServerRecordBatchStreamReader requestStream,
            IAsyncStreamWriter<FlightPutResult> responseStream,
            ServerCallContext context
        )
        {
            RequireAuthorization(context);
            if (
                await FlightSqlServer.GetCommand(requestStream).ConfigureAwait(false)
                is not CommandStatementUpdate update
            )
            {
                throw new RpcException(
                    new Status(StatusCode.InvalidArgument, "Expected a statement update.")
                );
            }

            _capture.LastUpdate = update.Query;
            ThrowIfSensitiveFailure("update");
            _capture.LastUpdateTransactionId = update.HasTransactionId
                ? update.TransactionId.ToStringUtf8()
                : null;
            var result = new DoPutUpdateResult { RecordCount = 3 };
            await responseStream
                .WriteAsync(new FlightPutResult(result.ToByteString()))
                .ConfigureAwait(false);
        }

        public override async Task DoAction(
            FlightAction request,
            IAsyncStreamWriter<FlightResult> responseStream,
            ServerCallContext context
        )
        {
            RequireAuthorization(context);
            _capture.LastAction = request.Type;
            if (request.Type == "BeginTransaction")
            {
                ThrowIfSensitiveFailure("begin");
                _ = Any.Parser.ParseFrom(request.Body).Unpack<ActionBeginTransactionRequest>();
                var result = new ActionBeginTransactionResult
                {
                    TransactionId = ByteString.CopyFromUtf8("transaction-1"),
                };
                await responseStream
                    .WriteAsync(new FlightResult(Any.Pack(result).ToByteString()))
                    .ConfigureAwait(false);
            }
            else if (request.Type == "EndTransaction")
            {
                ThrowIfSensitiveFailure("end");
                ActionEndTransactionRequest end = Any
                    .Parser.ParseFrom(request.Body)
                    .Unpack<ActionEndTransactionRequest>();
                _capture.LastEndTransactionAction = (int)end.Action;
                if (_capture.FailEndTransaction)
                {
                    throw new RpcException(
                        new Status(StatusCode.Internal, "End transaction failed.")
                    );
                }
            }
            else if (request.Type == "CloseSession")
            {
                Interlocked.Increment(ref _capture.ClosedSessions);
                await responseStream
                    .WriteAsync(new FlightResult(ByteString.Empty))
                    .ConfigureAwait(false);
            }
        }

        private void ThrowIfSensitiveFailure(string phase)
        {
            if (_capture.FailurePhase == phase)
            {
                throw new RpcException(
                    new Status(StatusCode.InvalidArgument, "Server echoed " + SensitiveValue),
                    new Metadata { { "private-data", SensitiveValue } }
                );
            }
        }

        protected static string? ReadAuthorization(ServerCallContext context) =>
            context.RequestHeaders.FirstOrDefault(header => header.Key == "authorization")?.Value;

        /// <summary>
        /// Accepts the session token, and counts every call that still carries raw credentials.
        /// </summary>
        private void RequireAuthorization(ServerCallContext context)
        {
            string? authorization = ReadAuthorization(context);
            if (authorization == SessionAuthorization)
            {
                Interlocked.Increment(ref _capture.SessionAuthorizedCalls);
                return;
            }

            if (authorization == ExpectedBasicAuthorization)
            {
                Interlocked.Increment(ref _capture.BasicAuthorizedCalls);
                return;
            }

            throw new RpcException(new Status(StatusCode.Unauthenticated, "Unauthorized."));
        }
    }

    /// <summary>
    /// Models a Flight server that does not implement the handshake at all.
    /// </summary>
    private sealed class BasicOnlyFlightServer : AuthorizedFlightServer
    {
        public BasicOnlyFlightServer(QueryCapture capture)
            : base(capture) { }

        public override Task Handshake(
            IAsyncStreamReader<FlightHandshakeRequest> requestStream,
            IAsyncStreamWriter<FlightHandshakeResponse> responseStream,
            ServerCallContext context
        ) => throw new RpcException(new Status(StatusCode.Unimplemented, "No handshake."));
    }

    private sealed class QueryCapture
    {
        public int Handshakes;
        public int SessionAuthorizedCalls;
        public int BasicAuthorizedCalls;
        public int ClosedSessions;

        public TaskCompletionSource DoGetStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CommittedWrites;

        public string? FailurePhase { get; set; }

        public string LastQuery { get; set; } = string.Empty;

        /// <summary>This server's own address, advertised as an endpoint location on request.</summary>
        public string SelfAddress { get; set; } = string.Empty;

        public string LastUpdate { get; set; } = string.Empty;

        public string? LastUpdateTransactionId { get; set; }

        public string LastAction { get; set; } = string.Empty;

        public int LastEndTransactionAction { get; set; }

        public bool FailEndTransaction { get; set; }
    }

    private sealed class FlightContext(DbContextOptions<FlightContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().ToTable("widgets").HasKey(widget => widget.Id);
            modelBuilder.Entity<Widget>().Property(widget => widget.Id).ValueGeneratedNever();
        }
    }

    private sealed class Widget
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
