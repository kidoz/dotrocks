using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using DotRocks.Data;
using DotRocks.Data.Loading;
using DotRocks.Data.Pooling;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

/// <summary>
/// Fake-server regression tests for connection recovery semantics: large multi-packet commands,
/// early reader disposal, server errors on reusable-but-retired sessions, prepared-statement error
/// surfacing, and credential flow through <see cref="DotRocksDataSource"/>.
/// </summary>
public sealed class DotRocksConnectionRecoveryTests
{
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
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            // The reader reassembles the multi-packet request; the reply continues its sequence.
            await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(server.ConnectionString);
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
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            commands.Add(
                await FakeStarRocksServer
                    .ReadCommandAndReplyResultSetAsync(stream, "1", "2", "3")
                    .ConfigureAwait(true)
            );
            commands.Add(
                await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true)
            );
        });

        using var connection = new DotRocksConnection(server.ConnectionString);
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
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer
                .ReadCommandAndReplyResultSetAsync(stream, "1", "2")
                .ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(server.ConnectionString);
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
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer
                .ReadCommandAndReplyResultSetAsync(stream, "1", "2")
                .ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(server.ConnectionString);
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
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            // OK for SET, an ERR for the failing SELECT, then OK for the follow-up command.
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
            _ = await FakeStarRocksServer
                .ReadCommandAndReplyErrorAsync(stream)
                .ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(server.ConnectionString);
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
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            await HandlePrepareAsync(stream).ConfigureAwait(true);
            await HandleExecuteWithErrorMidRowsAsync(stream).ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(server.ConnectionString);
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
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ClosePreparedStatement_PreCanceledToken_MarksConnectionBroken()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(true);
        });
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            server.ConnectionString
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
            await FakeStarRocksServer
                .CompleteAuthenticationVerifyingScrambleAsync(stream)
                .ConfigureAwait(true);
            _ = await FakeStarRocksServer.ReadCommandAndReplyOkAsync(stream).ConfigureAwait(true);
        });

        using var dataSource = new DotRocksDataSource(server.ConnectionString);

        // The public surface is redacted...
        Assert.DoesNotContain(
            FakeStarRocksServer.Secret,
            dataSource.ConnectionString,
            StringComparison.Ordinal
        );

        // ...but connections created by the data source still carry the real password: the fake
        // server verifies the mysql_native_password scramble and rejects a missing/wrong secret.
        DbConnection connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(true);
        await using (connection.ConfigureAwait(true))
        {
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.DoesNotContain(
                FakeStarRocksServer.Secret,
                connection.ConnectionString,
                StringComparison.Ordinal
            );

            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
        }
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
        await writer.WritePayloadAsync(StarRocksPacketFactory.PrepareOk(), ct).ConfigureAwait(true);
        await writer
            .WritePayloadAsync(StarRocksPacketFactory.ColumnDefinition("value"), ct)
            .ConfigureAwait(true);
        await writer.WritePayloadAsync(StarRocksPacketFactory.Eof(), ct).ConfigureAwait(true);
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
        await writer.WritePayloadAsync(new byte[] { 0x01 }, ct).ConfigureAwait(true); // column count
        await writer
            .WritePayloadAsync(StarRocksPacketFactory.ColumnDefinition("value"), ct)
            .ConfigureAwait(true);
        await writer.WritePayloadAsync(StarRocksPacketFactory.Eof(), ct).ConfigureAwait(true);
        await writer
            .WritePayloadAsync(StarRocksPacketFactory.Error("Query aborted by server"), ct)
            .ConfigureAwait(true);
    }
}
