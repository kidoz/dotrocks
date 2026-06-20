using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotRocks.Data;
using DotRocks.Data.Authentication;
using DotRocks.Data.Diagnostics;
using DotRocks.Data.Loading;
using DotRocks.Data.Pooling;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class DotRocksConnectionCancellationTests
{
    private const string Secret = "fake-server-secret";
    private static readonly byte[] AuthPart1 = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] AuthPart2 = [9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 0];

    [Fact]
    public async Task DisposingUncommittedTransaction_RollsBackAndKeepsConnectionUsable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var commands = new List<string>();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            commands.Add(await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true));
            commands.Add(await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true));
            commands.Add(await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true));
        });

        using var connection = new DotRocksConnection(BuildFakeServerConnectionString(server.Port));
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
            await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using (
            var connection = new DotRocksConnection(BuildFakeServerConnectionString(server.Port))
        )
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
    public async Task Pool_WarmUp_PreOpensMinimumPoolSizeConnections()
    {
        // The fake server serves connections sequentially, so this verifies warm-up with a single
        // pre-opened connection (Minimum Pool Size = 1).
        using var server = FakeStarRocksServer.Start(HandleAuthThenKeepOpenAsync);
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            BuildFakeServerConnectionString(server.Port)
                + ";Pooling=true;Minimum Pool Size=1;Maximum Pool Size=4"
        );
        DotRocksConnectionPool pool = DotRocksConnectionPool.GetPool(options);
        try
        {
            await pool.WarmUpAsync().ConfigureAwait(true);
            Assert.Equal(1, pool.IdleCount);
        }
        finally
        {
            pool.Dispose();
        }
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
                await CompleteAuthenticationAsync(stream).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            },
            ct
        );

        string connectionString =
            $"Server=127.0.0.1;Port={port};User ID=alice;Password={Secret};Connection Timeout=5;Connection Retries=2;Connection Retry Delay=50";
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
            $"Server=127.0.0.1;Port={port};User ID=alice;Password={Secret};Connection Timeout=5";
        using var connection = new DotRocksConnection(connectionString);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        OperationCanceledException exception = await Assert
            .ThrowsAsync<OperationCanceledException>(async () =>
                await connection.OpenAsync(cancellation.Token).ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        using TcpClient acceptedClient = await acceptedClientTask.ConfigureAwait(true);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
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
        string connectionString = BuildFakeServerConnectionString(server.Port);
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
                    BuildHandshake("caching_sha2_password"),
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        });
        string connectionString = BuildFakeServerConnectionString(server.Port);
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
                    BuildHandshake(MySqlNativePassword.PluginName),
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            var reader = new PacketReader(stream);
            reader.ResetSequence(writer.SequenceId);
            _ = await reader
                .ReadPayloadAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        });
        string connectionString = BuildFakeServerConnectionString(server.Port);
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
                    BuildHandshake(MySqlNativePassword.PluginName),
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        });
        string connectionString =
            BuildFakeServerConnectionString(server.Port) + ";Ssl Mode=Required";
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
        using var server = FakeStarRocksServer.Start(HandleTlsOpenOnlyConnectionAsync);
        string connectionString =
            BuildFakeServerConnectionString(server.Port)
            + ";Ssl Mode=Required;Trust Server Certificate=True";
        using var connection = new DotRocksConnection(connectionString);

        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task OpenAsync_SslModeRequiredWithUntrustedCertificate_RejectsConnection()
    {
        using var server = FakeStarRocksServer.Start(HandleTlsRejectingConnectionAsync);
        string connectionString =
            BuildFakeServerConnectionString(server.Port) + ";Ssl Mode=Required";
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
    public async Task ExecuteScalarAsync_MalformedQueryResult_ThrowsSanitizedExceptionAndDiscardsPooledConnection()
    {
        using var server = FakeStarRocksServer.Start(
            HandleMalformedQueryConnectionAsync,
            HandleOpenOnlyConnectionAsync
        );
        string connectionString =
            BuildFakeServerConnectionString(server.Port)
            + ";Pooling=True;Maximum Pool Size=1;Minimum Pool Size=0";
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
        await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
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

    private static async Task HandleOpenOnlyConnectionAsync(NetworkStream stream)
    {
        await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static async Task HandleTlsRejectingConnectionAsync(NetworkStream stream)
    {
        var writer = new PacketWriter(stream);
        await writer
            .WritePayloadAsync(
                BuildHandshake(MySqlNativePassword.PluginName, supportsTls: true),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var reader = new PacketReader(stream);
        reader.ResetSequence(writer.SequenceId);
        _ = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        using X509Certificate2 certificate = CreateSelfSignedCertificate();
        using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
        try
        {
            await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        EnabledSslProtocols = SslProtocols.None,
                        ClientCertificateRequired = false,
                    },
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        }
        catch (AuthenticationException)
        {
            // Expected: the client rejects the untrusted certificate and aborts the handshake.
        }
        catch (IOException)
        {
            // Expected: the client closes the socket once it rejects the certificate.
        }
    }

    private static async Task HandleTlsOpenOnlyConnectionAsync(NetworkStream stream)
    {
        var writer = new PacketWriter(stream);
        await writer
            .WritePayloadAsync(
                BuildHandshake(MySqlNativePassword.PluginName, supportsTls: true),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        var reader = new PacketReader(stream);
        reader.ResetSequence(writer.SequenceId);
        byte[] sslRequestPayload = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        AssertSslRequest(sslRequestPayload);
        byte authSequence = reader.SequenceId;

        using X509Certificate2 certificate = CreateSelfSignedCertificate();
        using var tls = new SslStream(stream, true);
        await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.None,
                    ClientCertificateRequired = false,
                },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        reader = new PacketReader(tls);
        reader.ResetSequence(authSequence);
        _ = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        writer = new PacketWriter(tls);
        writer.ResetSequence(reader.SequenceId);
        await writer
            .WritePayloadAsync(BuildOkPayload(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static async Task HandleAuthThenKeepOpenAsync(NetworkStream stream)
    {
        await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
        byte[] buffer = new byte[256];
        try
        {
            while (
                await stream
                    .ReadAsync(buffer, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true) > 0
            )
            {
                // Drain anything sent; keep the socket open until the client closes it.
            }
        }
        catch (IOException)
        {
            // The client closed the pooled connection; the keep-alive loop ends.
        }
        catch (OperationCanceledException)
        {
            // The fake server is shutting down.
        }
    }

    private static async Task CompleteAuthenticationAsync(NetworkStream stream)
    {
        var writer = new PacketWriter(stream);
        await writer
            .WritePayloadAsync(
                BuildHandshake(MySqlNativePassword.PluginName),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var reader = new PacketReader(stream);
        reader.ResetSequence(writer.SequenceId);
        _ = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        writer.ResetSequence(reader.SequenceId);
        await writer
            .WritePayloadAsync(BuildOkPayload(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static string BuildFakeServerConnectionString(int port) =>
        $"Server=127.0.0.1;Port={port};User ID=alice;Password={Secret};Connection Timeout=5";

    private static byte[] BuildHandshake(string authPluginName, bool supportsTls = false)
    {
        CapabilityFlags capabilities =
            CapabilityFlags.LongPassword
            | CapabilityFlags.LongFlag
            | CapabilityFlags.Protocol41
            | CapabilityFlags.SecureConnection
            | CapabilityFlags.PluginAuth;
        if (supportsTls)
        {
            capabilities |= CapabilityFlags.Ssl;
        }
        uint caps = (uint)capabilities;
        using var writer = new ProtocolWriter();
        writer.WriteByte(10);
        writer.WriteNullTerminatedString("8.0.33-StarRocks-3.3", Encoding.ASCII);
        writer.WriteFixedInteger(42, 4);
        writer.WriteBytes(AuthPart1);
        writer.WriteByte(0);
        writer.WriteFixedInteger(caps & 0xFFFF, 2);
        writer.WriteByte(0x21);
        writer.WriteFixedInteger(2, 2);
        writer.WriteFixedInteger((caps >> 16) & 0xFFFF, 2);
        writer.WriteByte(21);
        writer.WriteBytes(new byte[10]);
        writer.WriteBytes(AuthPart2);
        writer.WriteNullTerminatedString(authPluginName, Encoding.ASCII);
        return writer.ToArray();
    }

    private static void AssertSslRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtocolReader(payload);
        var capabilities = (CapabilityFlags)reader.ReadFixedInteger(4);
        Assert.True(capabilities.HasFlag(CapabilityFlags.Ssl));
        Assert.True(capabilities.HasFlag(CapabilityFlags.Protocol41));
        Assert.True(capabilities.HasFlag(CapabilityFlags.SecureConnection));
        Assert.Equal(0UL, reader.ReadFixedInteger(4));
        Assert.Equal(0x21, reader.ReadByte());
        reader.ReadBytes(23);
        Assert.True(reader.IsAtEnd);
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=127.0.0.1",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1)
        );
    }

    private static async Task<string> ReadCommandAndReplyOkAsync(NetworkStream stream)
    {
        var reader = new PacketReader(stream);
        reader.ResetSequence(0);
        byte[] payload = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var writer = new PacketWriter(stream);
        writer.ResetSequence(reader.SequenceId);
        await writer
            .WritePayloadAsync(BuildOkPayload(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // COM_QUERY payload is a 0x03 command byte followed by the SQL text.
        return Encoding.UTF8.GetString(payload.AsSpan(1));
    }

    private static byte[] BuildOkPayload()
    {
        using var writer = new ProtocolWriter();
        writer.WriteByte(ResultPacket.OkHeader);
        writer.WriteLengthEncodedInteger(0);
        writer.WriteLengthEncodedInteger(0);
        writer.WriteFixedInteger(2, 2);
        writer.WriteFixedInteger(0, 2);
        return writer.ToArray();
    }

    private static void AssertSanitized(Exception exception, string connectionString)
    {
        string text = exception.ToString();
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, text, StringComparison.Ordinal);
    }

    private sealed class FakeStarRocksServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<NetworkStream, Task>[] _handlers;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _acceptLoop;
        private int _connectionCount;

        private FakeStarRocksServer(TcpListener listener, Func<NetworkStream, Task>[] handlers)
        {
            _listener = listener;
            _handlers = handlers;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync();
        }

        public int Port { get; }

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public static FakeStarRocksServer Start(params Func<NetworkStream, Task>[] handlers)
        {
            var listener = new TcpListener(IPAddress.Loopback, port: 0);
            listener.Start();
            return new FakeStarRocksServer(listener, handlers);
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                _acceptLoop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }

            _cancellation.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                using TcpClient client = await _listener
                    .AcceptTcpClientAsync(_cancellation.Token)
                    .ConfigureAwait(true);
                int index = Interlocked.Increment(ref _connectionCount) - 1;
                Func<NetworkStream, Task> handler =
                    index < _handlers.Length
                        ? _handlers[index]
                        : throw new InvalidOperationException(
                            "The fake StarRocks server received an unexpected connection."
                        );
                using NetworkStream stream = client.GetStream();
                await handler(stream).ConfigureAwait(true);
            }
        }
    }
}
