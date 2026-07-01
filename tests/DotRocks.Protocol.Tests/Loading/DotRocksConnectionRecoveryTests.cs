using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotRocks.Data;
using DotRocks.Data.Authentication;
using DotRocks.Data.Loading;
using DotRocks.Data.Pooling;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

/// <summary>
/// Fake-server regression tests for connection recovery semantics: large multi-packet commands,
/// early reader disposal, server errors on reusable-but-retired sessions, prepared-statement error
/// surfacing, and credential flow through <see cref="DotRocksDataSource"/>.
/// </summary>
public sealed class DotRocksConnectionRecoveryTests
{
    private const string Secret = "fake-server-secret";
    private static readonly byte[] AuthPart1 = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] AuthPart2 = [9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 0];

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test command text is a synthesized constant literal, not user input."
    )]
    public async Task LargeCommand_SpanningMultiplePackets_ReadsResponseAtContinuedSequence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            // The reader reassembles the multi-packet request; the reply continues its sequence.
            await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(BuildFakeServerConnectionString(server.Port));
        await connection.OpenAsync(ct).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        // A literal of exactly the per-packet maximum guarantees the COM_QUERY payload exceeds
        // 0xFFFFFF bytes and is split into two request packets, so the response arrives with
        // sequence id 2, not 1.
        command.CommandText =
            "SELECT '" + new string('a', MySqlPacket.MaxPacketPayloadLength) + "'";

        int affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(true);

        Assert.Equal(0, affected);
    }

    [Fact]
    public async Task DisposingPartiallyReadReader_DrainsRowsAndKeepsConnectionOpen()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var commands = new List<string>();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            commands.Add(
                await ReadCommandAndReplyResultSetAsync(stream, "1", "2", "3").ConfigureAwait(true)
            );
            commands.Add(await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true));
        });

        using var connection = new DotRocksConnection(BuildFakeServerConnectionString(server.Port));
        await connection.OpenAsync(ct).ConfigureAwait(true);

        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM t";
            DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(true);
            await using (reader.ConfigureAwait(true))
            {
                // Read only the first of three rows, then dispose: the remaining rows must be
                // drained and the logical connection must stay open and usable.
                Assert.True(await reader.ReadAsync(ct).ConfigureAwait(true));
                Assert.Equal("1", reader.GetValue(0));
            }
        }

        Assert.Equal(ConnectionState.Open, connection.State);

        using DbCommand next = connection.CreateCommand();
        next.CommandText = "SELECT 1";
        _ = await next.ExecuteNonQueryAsync(ct).ConfigureAwait(true);

        Assert.Equal(["SELECT value FROM t", "SELECT 1"], commands);
    }

    [Fact]
    public async Task SingleRowBehavior_SurfacesOneRowAndKeepsConnectionUsable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await ReadCommandAndReplyResultSetAsync(stream, "1", "2").ConfigureAwait(true);
            _ = await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(BuildFakeServerConnectionString(server.Port));
        await connection.OpenAsync(ct).ConfigureAwait(true);

        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM t";
            DbDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SingleRow, ct)
                .ConfigureAwait(true);
            await using (reader.ConfigureAwait(true))
            {
                Assert.True(await reader.ReadAsync(ct).ConfigureAwait(true));
                Assert.Equal("1", reader.GetValue(0));
                Assert.False(await reader.ReadAsync(ct).ConfigureAwait(true));
            }
        }

        Assert.Equal(ConnectionState.Open, connection.State);

        using DbCommand next = connection.CreateCommand();
        next.CommandText = "SELECT 1";
        _ = await next.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
    }

    [Fact]
    public async Task SchemaOnlyBehavior_ExposesMetadataWithoutRowsAndKeepsConnectionUsable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await ReadCommandAndReplyResultSetAsync(stream, "1", "2").ConfigureAwait(true);
            _ = await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(BuildFakeServerConnectionString(server.Port));
        await connection.OpenAsync(ct).ConfigureAwait(true);

        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM t";
            DbDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct)
                .ConfigureAwait(true);
            await using (reader.ConfigureAwait(true))
            {
                Assert.Equal(1, reader.FieldCount);
                Assert.Equal("value", reader.GetName(0));
                Assert.False(reader.HasRows);
                Assert.False(await reader.ReadAsync(ct).ConfigureAwait(true));
            }
        }

        Assert.Equal(ConnectionState.Open, connection.State);

        using DbCommand next = connection.CreateCommand();
        next.CommandText = "SELECT 1";
        _ = await next.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
    }

    [Fact]
    public async Task ServerError_AfterSessionMutatingCommand_DoesNotCloseConnection()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true); // SET
            _ = await ReadCommandAndReplyErrorAsync(stream).ConfigureAwait(true); // failing SELECT
            _ = await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true); // follow-up
        });

        using var connection = new DotRocksConnection(BuildFakeServerConnectionString(server.Port));
        await connection.OpenAsync(ct).ConfigureAwait(true);

        // SET marks the session dirty for pool-reuse policy; that must not make a later benign
        // server error close the logical connection.
        using (DbCommand set = connection.CreateCommand())
        {
            set.CommandText = "SET time_zone = '+00:00'";
            _ = await set.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
        }

        using (DbCommand failing = connection.CreateCommand())
        {
            failing.CommandText = "SELECT broken";
            DotRocksException exception = await Assert
                .ThrowsAsync<DotRocksException>(async () =>
                    _ = await failing.ExecuteNonQueryAsync(ct).ConfigureAwait(true)
                )
                .ConfigureAwait(true);
            Assert.Equal(1064, exception.ServerErrorCode);
        }

        // The protocol state is clean after an ERR packet; the connection must remain usable.
        Assert.Equal(ConnectionState.Open, connection.State);

        using DbCommand next = connection.CreateCommand();
        next.CommandText = "SELECT 1";
        _ = await next.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
    }

    [Fact]
    public async Task PreparedExecute_ErrPacketMidRows_SurfacesServerError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            await HandlePrepareAsync(stream).ConfigureAwait(true);
            await HandleExecuteWithErrorMidRowsAsync(stream).ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(BuildFakeServerConnectionString(server.Port));
        await connection.OpenAsync(ct).ConfigureAwait(true);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM t";
        command.ParameterMode = DotRocksParameterMode.ServerPrepared;

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                _ = await command.ExecuteReaderAsync(ct).ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        // The real server error must surface, not a wrapped malformed-packet failure.
        Assert.Equal(1064, exception.ServerErrorCode);
        Assert.Contains("aborted by server", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ClosePreparedStatement_PreCanceledToken_MarksConnectionBroken()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(true);
        });
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            BuildFakeServerConnectionString(server.Port)
        );

        using DotRocksPhysicalConnection physical = await DotRocksPhysicalConnection
            .OpenAsync(options, ct)
            .ConfigureAwait(true);

        await Assert
            .ThrowsAnyAsync<OperationCanceledException>(async () =>
                await physical
                    .ClosePreparedStatementAsync(5, new CancellationToken(canceled: true))
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        // A cancellation mid-COM_STMT_CLOSE may leave a partial frame; the connection must be
        // treated as broken so it can never be returned to the pool.
        Assert.True(physical.IsBroken);
        Assert.False(physical.IsReusable);
    }

    [Fact]
    public async Task DataSource_OpenConnection_AuthenticatesWithConfiguredPassword()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await CompleteAuthenticationVerifyingScrambleAsync(stream).ConfigureAwait(true);
            _ = await ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using var dataSource = new DotRocksDataSource(BuildFakeServerConnectionString(server.Port));

        // The public surface is redacted...
        Assert.DoesNotContain(Secret, dataSource.ConnectionString, StringComparison.Ordinal);

        // ...but connections created by the data source still carry the real password: the fake
        // server verifies the mysql_native_password scramble and rejects a missing/wrong secret.
        DbConnection connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(true);
        await using (connection.ConfigureAwait(true))
        {
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.DoesNotContain(Secret, connection.ConnectionString, StringComparison.Ordinal);

            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
        }
    }

    private static string BuildFakeServerConnectionString(int port) =>
        $"Server=127.0.0.1;Port={port};User ID=alice;Password={Secret};Connection Timeout=5";

    private static byte[] BuildHandshake(string authPluginName)
    {
        CapabilityFlags capabilities =
            CapabilityFlags.LongPassword
            | CapabilityFlags.LongFlag
            | CapabilityFlags.Protocol41
            | CapabilityFlags.SecureConnection
            | CapabilityFlags.PluginAuth;
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

    private static async Task CompleteAuthenticationVerifyingScrambleAsync(NetworkStream stream)
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
        byte[] response = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        writer.ResetSequence(reader.SequenceId);
        byte[] reply = AuthResponseMatchesSecret(response)
            ? BuildOkPayload()
            : BuildErrorPayload("Access denied");
        await writer
            .WritePayloadAsync(reply, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static bool AuthResponseMatchesSecret(byte[] handshakeResponse)
    {
        // The 20-byte challenge is part 1 plus part 2 without its trailing NUL.
        byte[] challenge = new byte[AuthPart1.Length + AuthPart2.Length - 1];
        AuthPart1.CopyTo(challenge, 0);
        AuthPart2.AsSpan(0, AuthPart2.Length - 1).CopyTo(challenge.AsSpan(AuthPart1.Length));
        byte[] expected = MySqlNativePassword.CreateAuthenticationResponse(Secret, challenge);

        var reader = new ProtocolReader(handshakeResponse);
        _ = reader.ReadFixedInteger(4); // capabilities
        _ = reader.ReadFixedInteger(4); // max packet size
        _ = reader.ReadByte(); // character set
        _ = reader.ReadBytes(23); // reserved
        string user = reader.ReadNullTerminatedString(Encoding.UTF8);
        ReadOnlySpan<byte> authResponse = reader.ReadLengthEncodedBytes(out _);
        return user == "alice" && authResponse.SequenceEqual(expected);
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

    private static async Task<string> ReadCommandAndReplyErrorAsync(NetworkStream stream)
    {
        var reader = new PacketReader(stream);
        reader.ResetSequence(0);
        byte[] payload = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var writer = new PacketWriter(stream);
        writer.ResetSequence(reader.SequenceId);
        await writer
            .WritePayloadAsync(
                BuildErrorPayload("Unknown column 'broken'"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        return Encoding.UTF8.GetString(payload.AsSpan(1));
    }

    private static async Task<string> ReadCommandAndReplyResultSetAsync(
        NetworkStream stream,
        params string[] rows
    )
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var reader = new PacketReader(stream);
        reader.ResetSequence(0);
        byte[] payload = await reader.ReadPayloadAsync(ct).ConfigureAwait(true);

        var writer = new PacketWriter(stream);
        writer.ResetSequence(reader.SequenceId);
        await writer.WritePayloadAsync(new byte[] { 0x01 }, ct).ConfigureAwait(true);
        await writer.WritePayloadAsync(BuildColumnDefinition("value"), ct).ConfigureAwait(true);
        await writer.WritePayloadAsync(EofPayload(), ct).ConfigureAwait(true);
        foreach (string row in rows)
        {
            await writer.WritePayloadAsync(BuildTextRow(row), ct).ConfigureAwait(true);
        }

        await writer.WritePayloadAsync(EofPayload(), ct).ConfigureAwait(true);
        return Encoding.UTF8.GetString(payload.AsSpan(1));
    }

    // Replies to COM_STMT_PREPARE for a one-column, zero-parameter statement: the prepare-OK
    // header packet, then the column-definition block terminated by EOF.
    private static async Task HandlePrepareAsync(NetworkStream stream)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var reader = new PacketReader(stream);
        reader.ResetSequence(0);
        _ = await reader.ReadPayloadAsync(ct).ConfigureAwait(true);

        var writer = new PacketWriter(stream);
        writer.ResetSequence(reader.SequenceId);
        await writer.WritePayloadAsync(BuildPrepareOkPayload(), ct).ConfigureAwait(true);
        await writer.WritePayloadAsync(BuildColumnDefinition("value"), ct).ConfigureAwait(true);
        await writer.WritePayloadAsync(EofPayload(), ct).ConfigureAwait(true);
    }

    // Replies to COM_STMT_EXECUTE with the column block and then an ERR packet in place of the
    // first row, as a server that aborts the query mid-stream does.
    private static async Task HandleExecuteWithErrorMidRowsAsync(NetworkStream stream)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var reader = new PacketReader(stream);
        reader.ResetSequence(0);
        _ = await reader.ReadPayloadAsync(ct).ConfigureAwait(true);

        var writer = new PacketWriter(stream);
        writer.ResetSequence(reader.SequenceId);
        await writer.WritePayloadAsync(new byte[] { 0x01 }, ct).ConfigureAwait(true);
        await writer.WritePayloadAsync(BuildColumnDefinition("value"), ct).ConfigureAwait(true);
        await writer.WritePayloadAsync(EofPayload(), ct).ConfigureAwait(true);
        await writer
            .WritePayloadAsync(BuildErrorPayload("Query aborted by server"), ct)
            .ConfigureAwait(true);
    }

    private static byte[] BuildPrepareOkPayload()
    {
        using var writer = new ProtocolWriter();
        writer.WriteByte(0x00); // status
        writer.WriteFixedInteger(9, 4); // statement id
        writer.WriteFixedInteger(1, 2); // column count
        writer.WriteFixedInteger(0, 2); // parameter count
        writer.WriteByte(0); // filler
        writer.WriteFixedInteger(0, 2); // warning count
        return writer.ToArray();
    }

    private static byte[] BuildColumnDefinition(string name)
    {
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedString("def", Encoding.UTF8);
        writer.WriteLengthEncodedString(string.Empty, Encoding.UTF8);
        writer.WriteLengthEncodedString(string.Empty, Encoding.UTF8);
        writer.WriteLengthEncodedString(string.Empty, Encoding.UTF8);
        writer.WriteLengthEncodedString(name, Encoding.UTF8);
        writer.WriteLengthEncodedString(name, Encoding.UTF8);
        writer.WriteLengthEncodedInteger(0x0C);
        writer.WriteFixedInteger(0x21, 2);
        writer.WriteFixedInteger(1024, 4);
        writer.WriteByte((byte)ColumnType.VarString);
        writer.WriteFixedInteger(0, 2);
        writer.WriteByte(0);
        writer.WriteFixedInteger(0, 2);
        return writer.ToArray();
    }

    private static byte[] BuildTextRow(string value)
    {
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedString(value, Encoding.UTF8);
        return writer.ToArray();
    }

    private static byte[] EofPayload() => [0xFE, 0x00, 0x00, 0x02, 0x00];

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

    private static byte[] BuildErrorPayload(string message)
    {
        using var writer = new ProtocolWriter();
        writer.WriteByte(ProtocolConstants.ErrorPacketHeader);
        writer.WriteFixedInteger(1064, 2);
        writer.WriteByte((byte)'#');
        writer.WriteBytes(Encoding.ASCII.GetBytes("42000"));
        writer.WriteBytes(Encoding.UTF8.GetBytes(message));
        return writer.ToArray();
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
