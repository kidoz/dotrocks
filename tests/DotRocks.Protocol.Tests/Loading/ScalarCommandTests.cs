using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using DotRocks.Data;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Results;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class ScalarCommandTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [SuppressMessage(
        "Performance",
        "CA1849:Call async methods when in an async method",
        Justification = "Tests both synchronous and asynchronous scalar contracts."
    )]
    public async Task ExecuteScalar_DistinguishesNullFromNoRows(bool asynchronous, bool hasRow)
    {
        using var server = FakeStarRocksServer.Start(stream =>
            ReplyAsync(stream, hasRow ? [0xfb] : null)
        );
        using var connection = new DotRocksConnection(server.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NULL";

        object? value = asynchronous
            ? await command
                .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
            : command.ExecuteScalar();

        if (hasRow)
        {
            Assert.Same(DBNull.Value, value);
        }
        else
        {
            Assert.Null(value);
        }
    }

    private static async Task ReplyAsync(NetworkStream stream, byte[]? row)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
        var reader = new PacketReader(stream);
        _ = await reader.ReadPayloadAsync(token).ConfigureAwait(true);
        var writer = new PacketWriter(stream);
        writer.ResetSequence(reader.SequenceId);
        await writer.WritePayloadAsync(new byte[] { 1 }, token).ConfigureAwait(true);
        await writer
            .WritePayloadAsync(
                StarRocksPacketFactory.ColumnDefinition("value", (byte)ColumnType.Long),
                token
            )
            .ConfigureAwait(true);
        await writer.WritePayloadAsync(StarRocksPacketFactory.Eof(), token).ConfigureAwait(true);
        if (row is not null)
        {
            await writer.WritePayloadAsync(row, token).ConfigureAwait(true);
        }

        await writer.WritePayloadAsync(StarRocksPacketFactory.Eof(), token).ConfigureAwait(true);
    }
}
