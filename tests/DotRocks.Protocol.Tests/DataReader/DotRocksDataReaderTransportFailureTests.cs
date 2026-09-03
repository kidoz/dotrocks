using System.Data;
using System.Data.Common;
using DotRocks.Data;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.DataReader;

/// <summary>
/// A socket that dies while rows are streaming must surface through the public exception model,
/// exactly like one that dies while the command is being submitted. Before this was pinned, the
/// row loop rethrew the raw <c>IOException</c> or the internal malformed-packet type, so callers
/// could neither catch <see cref="DotRocksException"/> nor consult <c>IsTransient</c>.
/// </summary>
public sealed class DotRocksDataReaderTransportFailureTests
{
    [Fact]
    public async Task ReadAsync_WhenServerDropsConnectionMidResultSet_ThrowsTransientDotRocksException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            await FakeStarRocksServer
                .ReadCommandAndReplyTruncatedResultSetAsync(stream, "1")
                .ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(server.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(true);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM dropped";

        using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(ct).ConfigureAwait(true));

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(() => reader.ReadAsync(ct))
            .ConfigureAwait(true);

        Assert.True(exception.IsTransient);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void Read_WhenServerDropsConnectionMidResultSet_ThrowsTransientDotRocksException()
    {
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            await FakeStarRocksServer
                .ReadCommandAndReplyTruncatedResultSetAsync(stream, "1")
                .ConfigureAwait(true);
        });

        using var connection = new DotRocksConnection(server.ConnectionString);
        connection.Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM dropped";

        using DbDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());

        DotRocksException exception = Assert.Throws<DotRocksException>(() => reader.Read());

        Assert.True(exception.IsTransient);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
