using System.Data;
using System.Net;
using System.Net.Sockets;
using DotRocks.Data;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class DotRocksConnectionCancellationTests
{
    [Fact]
    public async Task OpenAsync_CancellationWhileWaitingForHandshake_ClosesConnectionWithoutSecretLeak()
    {
        const string secret = "open-cancellation-secret";
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var acceptCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<TcpClient> acceptedClientTask = listener
            .AcceptTcpClientAsync(acceptCancellation.Token)
            .AsTask();
        string connectionString =
            $"Server=127.0.0.1;Port={port};User ID=alice;Password={secret};Connection Timeout=5";
        using var connection = new DotRocksConnection(connectionString);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        OperationCanceledException exception = await Assert
            .ThrowsAsync<OperationCanceledException>(async () =>
                await connection.OpenAsync(cancellation.Token).ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        using TcpClient acceptedClient = await acceptedClientTask.ConfigureAwait(true);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, exception.ToString(), StringComparison.Ordinal);
    }
}
