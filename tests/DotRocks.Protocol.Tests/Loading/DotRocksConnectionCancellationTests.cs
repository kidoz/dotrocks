using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using DotRocks.Data;
using DotRocks.Data.Authentication;
using DotRocks.Data.Diagnostics;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Serialization;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class DotRocksConnectionCancellationTests
{
    [Fact]
    public async Task Batch_ExecutesCommandsSequentiallyWithBoundParameters()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var commands = new List<string>();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            commands.Add(
                await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true)
            );
            commands.Add(
                await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true)
            );
        });

        using var connection = new DotRocksConnection(server.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(true);

        Assert.True(connection.CanCreateBatch);
        using DbBatch batch = connection.CreateBatch();

        DbBatchCommand first = batch.CreateBatchCommand();
        first.CommandText = "INSERT INTO t SELECT 1";
        batch.BatchCommands.Add(first);

        DbBatchCommand second = batch.CreateBatchCommand();
        second.CommandText = "INSERT INTO t SELECT @v";
        DbParameter parameter = second.CreateParameter();
        parameter.ParameterName = "@v";
        parameter.Value = 2;
        second.Parameters.Add(parameter);
        batch.BatchCommands.Add(second);

        int affected = await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(true);

        Assert.Equal(["INSERT INTO t SELECT 1", "INSERT INTO t SELECT 2"], commands);
        Assert.Equal(0, affected);
    }

    [Fact]
    public async Task DisposingUncommittedTransaction_RollsBackAndKeepsConnectionUsable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var commands = new List<string>();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            commands.Add(
                await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true)
            );
            commands.Add(
                await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true)
            );
            commands.Add(
                await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true)
            );
        });

        using var connection = new DotRocksConnection(server.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(true);

        DbTransaction transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(true);
        await using (transaction.ConfigureAwait(true))
        {
            // No Commit/Rollback: disposing must issue ROLLBACK WORK, not abort the connection.
        }

        Assert.Equal(ConnectionState.Open, connection.State);

        // The connection must still be usable after the rolled-back transaction.
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        _ = await command.ExecuteScalarAsync(ct).ConfigureAwait(true);

        Assert.Equal(["START TRANSACTION", "ROLLBACK WORK", "SELECT 1"], commands);
    }

    [Fact]
    public async Task OpenAndExecute_RecordTracingAndMetrics()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DotRocksTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(activityListener);

        long connectionsOpened = 0;
        long commandsExecuted = 0;
        bool durationRecorded = false;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == DotRocksTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, measurement, _, _) =>
            {
                if (instrument.Name == "dotrocks.connections.opened")
                {
                    Interlocked.Add(ref connectionsOpened, measurement);
                }
                else if (instrument.Name == "dotrocks.commands.executed")
                {
                    Interlocked.Add(ref commandsExecuted, measurement);
                }
            }
        );
        meterListener.SetMeasurementEventCallback<double>(
            (instrument, _, _, _) =>
            {
                if (instrument.Name == "dotrocks.command.duration")
                {
                    durationRecorded = true;
                }
            }
        );
        meterListener.Start();

        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using (var connection = new DotRocksConnection(server.ConnectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(true);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            _ = await command.ExecuteScalarAsync(ct).ConfigureAwait(true);
        }

        meterListener.Dispose();

        Assert.Contains(activities, a => a.OperationName == "dotrocks.connection.open");
        Assert.Contains(activities, a => a.OperationName == "dotrocks.command.execute");
        Assert.True(connectionsOpened >= 1);
        Assert.True(commandsExecuted >= 1);
        Assert.True(durationRecorded);
    }

    [Fact]
    public async Task OpenAsync_RetriesTransientFailure_WhenConnectionRetriesConfigured()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task serverTask = Task.Run(
            async () =>
            {
                // First attempt: accept then abort with RST to force a transient I/O failure.
                using (
                    TcpClient first = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false)
                )
                {
                    first.LingerState = new LingerOption(enable: true, seconds: 0);
                }

                // Second attempt: complete a normal handshake.
                using TcpClient second = await listener
                    .AcceptTcpClientAsync(ct)
                    .ConfigureAwait(false);
                using NetworkStream stream = second.GetStream();
                await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            },
            ct
        );

        string connectionString =
            $"Server=127.0.0.1;Port={port};User ID={FakeStarRocksServer.UserName};"
            + $"Password={FakeStarRocksServer.Secret};Connection Timeout=5;"
            + "Connection Retries=2;Connection Retry Delay=50";
        using var connection = new DotRocksConnection(connectionString);

        await connection.OpenAsync(ct).ConfigureAwait(true);

        Assert.Equal(ConnectionState.Open, connection.State);
        await serverTask.ConfigureAwait(true);
    }

    [Fact]
    public async Task OpenAsync_CancellationWhileWaitingForHandshake_ClosesConnectionWithoutSecretLeak()
    {
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var acceptCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<TcpClient> acceptedClientTask = listener
            .AcceptTcpClientAsync(acceptCancellation.Token)
            .AsTask();
        string connectionString =
            $"Server=127.0.0.1;Port={port};User ID={FakeStarRocksServer.UserName};"
            + $"Password={FakeStarRocksServer.Secret};Connection Timeout=5";
        using var connection = new DotRocksConnection(connectionString);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        OperationCanceledException exception = await Assert
            .ThrowsAsync<OperationCanceledException>(async () =>
                await connection.OpenAsync(cancellation.Token).ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        using TcpClient acceptedClient = await acceptedClientTask.ConfigureAwait(true);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.DoesNotContain(
            FakeStarRocksServer.Secret,
            exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(connectionString, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_MalformedHandshakePacket_ThrowsSanitizedDotRocksException()
    {
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            var writer = new PacketWriter(stream);
            await writer
                .WritePayloadAsync(
                    new byte[] { 10, (byte)'5' },
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        });
        string connectionString = server.ConnectionString;
        using var connection = new DotRocksConnection(connectionString);

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await connection
                    .OpenAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.IsType<MalformedPacketException>(exception.InnerException);
        AssertSanitized(exception, connectionString);
    }

    [Fact]
    public async Task OpenAsync_UnsupportedAuthPlugin_ThrowsSanitizedDotRocksException()
    {
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            var writer = new PacketWriter(stream);
            await writer
                .WritePayloadAsync(
                    StarRocksPacketFactory.Handshake("caching_sha2_password"),
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        });
        string connectionString = server.ConnectionString;
        using var connection = new DotRocksConnection(connectionString);

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await connection
                    .OpenAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Contains(
            "Unsupported StarRocks authentication plugin",
            exception.Message,
            StringComparison.Ordinal
        );
        AssertSanitized(exception, connectionString);
    }

    [Fact]
    public async Task OpenAsync_ServerClosesDuringAuthResult_ThrowsSanitizedDotRocksException()
    {
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            var writer = new PacketWriter(stream);
            await writer
                .WritePayloadAsync(
                    StarRocksPacketFactory.Handshake(MySqlNativePassword.PluginName),
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            var reader = new PacketReader(stream);
            reader.ResetSequence(writer.SequenceId);
            _ = await reader
                .ReadPayloadAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        });
        string connectionString = server.ConnectionString;
        using var connection = new DotRocksConnection(connectionString);

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await connection
                    .OpenAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Contains("malformed protocol bytes", exception.Message, StringComparison.Ordinal);
        AssertSanitized(exception, connectionString);
    }

    [Fact]
    public async Task OpenAsync_SslModeRequiredWithoutServerSupport_ThrowsSanitizedDotRocksException()
    {
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            var writer = new PacketWriter(stream);
            await writer
                .WritePayloadAsync(
                    StarRocksPacketFactory.Handshake(MySqlNativePassword.PluginName),
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        });
        string connectionString = server.ConnectionString + ";Ssl Mode=Required";
        using var connection = new DotRocksConnection(connectionString);

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await connection
                    .OpenAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Contains("TLS support", exception.Message, StringComparison.Ordinal);
        AssertSanitized(exception, connectionString);
    }

    [Fact]
    public async Task OpenAsync_SslModeRequired_UpgradesToTlsAndAuthenticates()
    {
        using var server = FakeStarRocksServer.Start(
            FakeStarRocksServer.HandleTlsOpenOnlyConnectionAsync
        );
        string connectionString =
            server.ConnectionString + ";Ssl Mode=Required;Trust Server Certificate=True";
        using var connection = new DotRocksConnection(connectionString);

        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task OpenAsync_SslModeRequiredWithUntrustedCertificate_RejectsConnection()
    {
        using var server = FakeStarRocksServer.Start(
            FakeStarRocksServer.HandleTlsRejectingConnectionAsync
        );
        string connectionString = server.ConnectionString + ";Ssl Mode=Required";
        using var connection = new DotRocksConnection(connectionString);

        // Without Trust Server Certificate, the self-signed certificate must be rejected.
        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await connection
                    .OpenAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        AssertSanitized(exception, connectionString);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task OpenAsync_SslModePreferredWithoutServerSupport_ConnectsInPlaintext()
    {
        using var server = FakeStarRocksServer.Start(
            FakeStarRocksServer.HandleOpenOnlyConnectionAsync
        );

        // Preferred is opportunistic: a server that does not advertise TLS continues in plaintext
        // rather than failing the connection.
        string connectionString = server.ConnectionString + ";Ssl Mode=Preferred";
        using var connection = new DotRocksConnection(connectionString);

        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ConnectionState.Open, connection.State);
        await connection.CloseAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task OpenAsync_SslModePreferredWithServerSupport_NegotiatesTlsAndValidatesCertificate()
    {
        using var server = FakeStarRocksServer.Start(
            FakeStarRocksServer.HandleTlsRejectingConnectionAsync
        );

        // Preferred upgrades when the server advertises TLS and then enforces certificate
        // validation; the self-signed cert is rejected (proving it negotiated TLS rather than
        // silently falling back to plaintext). Trust Server Certificate is not allowed under
        // Preferred, so this is the only way to exercise the upgrade path here.
        string connectionString = server.ConnectionString + ";Ssl Mode=Preferred";
        using var connection = new DotRocksConnection(connectionString);

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await connection
                    .OpenAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.IsType<AuthenticationException>(exception.InnerException);
        Assert.False(exception.IsTransient);
        Assert.Equal(ConnectionState.Closed, connection.State);
        AssertSanitized(exception, connectionString);
    }

    [Fact]
    public async Task ExecuteScalarAsync_MalformedQueryResult_ThrowsSanitizedExceptionAndDiscardsPooledConnection()
    {
        using var server = FakeStarRocksServer.Start(
            HandleMalformedQueryConnectionAsync,
            FakeStarRocksServer.HandleOpenOnlyConnectionAsync
        );
        string connectionString =
            server.ConnectionString + ";Pooling=True;Maximum Pool Size=1;Minimum Pool Size=0";
        DotRocksConnection.ClearAllPools();

        try
        {
            using (var first = new DotRocksConnection(connectionString))
            {
                await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                using DbCommand command = first.CreateCommand();
                command.CommandText = "SELECT 1";

                DotRocksException exception = await Assert
                    .ThrowsAsync<DotRocksException>(async () =>
                        await command
                            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                            .ConfigureAwait(true)
                    )
                    .ConfigureAwait(true);

                Assert.Equal(ConnectionState.Closed, first.State);
                Assert.Contains(
                    "malformed protocol bytes",
                    exception.Message,
                    StringComparison.Ordinal
                );
                AssertSanitized(exception, connectionString);
            }

            using (var second = new DotRocksConnection(connectionString))
            {
                await second.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                Assert.Equal(ConnectionState.Open, second.State);
                await second.CloseAsync().ConfigureAwait(true);
            }

            Assert.Equal(2, server.ConnectionCount);
        }
        finally
        {
            DotRocksConnection.ClearAllPools();
        }
    }

    private static async Task HandleMalformedQueryConnectionAsync(NetworkStream stream)
    {
        await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
        var reader = new PacketReader(stream);
        reader.ResetSequence(0);
        _ = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var writer = new PacketWriter(stream);
        writer.ResetSequence(1);
        await writer
            .WritePayloadAsync(Array.Empty<byte>(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static void AssertSanitized(Exception exception, string connectionString)
    {
        string text = exception.ToString();
        Assert.DoesNotContain(FakeStarRocksServer.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, text, StringComparison.Ordinal);
    }
}
