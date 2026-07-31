using System.Diagnostics.CodeAnalysis;
using System.Net;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Types;
using Arrow.Flight.Protocol.Sql;
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
public sealed class FlightSqlTransportTests
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

    private static IHost CreateHost() =>
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
                    services.AddGrpc().AddFlightServer<AuthorizedFlightServer>();
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

    private sealed class AuthorizedFlightServer : FlightServer
    {
        private const string ExpectedAuthorization = "Basic cm9vdDpzZWNyZXQ=";
        private static readonly Schema s_schema = new(
            [new Field("value", Int32Type.Default, false)],
            null
        );
        private static readonly Schema s_widgetSchema = new(
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
            bool widgets = query.Contains("widgets", StringComparison.OrdinalIgnoreCase);
            string ticket = widgets ? "widgets" : "result";
            Schema schema = widgets ? s_widgetSchema : s_schema;
            var endpoint = new FlightEndpoint(new FlightTicket(ticket), []);
            return Task.FromResult(new FlightInfo(schema, request, [endpoint], 2, -1));
        }

        public override async Task DoGet(
            FlightTicket ticket,
            FlightServerRecordBatchStreamWriter responseStream,
            ServerCallContext context
        )
        {
            RequireAuthorization(context);
            string ticketValue = ticket.Ticket.ToStringUtf8();
            if (ticketValue == "widgets")
            {
                using var ids = new Int32Array.Builder().Append(1).Append(2).Build();
                using var names = new StringArray.Builder().Append("alpha").Append("beta").Build();
                using var widgetBatch = new RecordBatch(s_widgetSchema, [ids, names], 2);
                await responseStream.WriteAsync(widgetBatch).ConfigureAwait(false);
                return;
            }

            if (ticketValue != "result")
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown ticket."));
            }

            using var values = new Int32Array.Builder().Append(1).Append(2).Build();
            using var batch = new RecordBatch(s_schema, [values], 2);
            await responseStream.WriteAsync(batch).ConfigureAwait(false);
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
                ActionEndTransactionRequest end = Any
                    .Parser.ParseFrom(request.Body)
                    .Unpack<ActionEndTransactionRequest>();
                _capture.LastEndTransactionAction = (int)end.Action;
            }
        }

        private static void RequireAuthorization(ServerCallContext context)
        {
            string? authorization = context
                .RequestHeaders.FirstOrDefault(header => header.Key == "authorization")
                ?.Value;
            if (authorization != ExpectedAuthorization)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Unauthorized."));
            }
        }
    }

    private sealed class QueryCapture
    {
        public string LastQuery { get; set; } = string.Empty;

        public string LastUpdate { get; set; } = string.Empty;

        public string? LastUpdateTransactionId { get; set; }

        public string LastAction { get; set; } = string.Empty;

        public int LastEndTransactionAction { get; set; }
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
