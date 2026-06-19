using System.Net.Sockets;
using DotRocks.Data.Loading;
using DotRocks.Data.Protocol.Commands;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Pooling;

internal sealed class DotRocksPhysicalConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private bool _isDisposed;
    private bool _isBroken;

    private DotRocksPhysicalConnection(TcpClient client, NetworkStream stream, string serverVersion)
    {
        _client = client;
        _stream = stream;
        ServerVersion = serverVersion;
    }

    public string ServerVersion { get; }

    public bool IsReusable => !_isDisposed && !_isBroken && _client.Connected;

    public static async ValueTask<DotRocksPhysicalConnection> OpenAsync(
        DotRocksConnectionOptions options,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        TcpClient? client = null;
        NetworkStream? stream = null;
        using var timeout = new CancellationTokenSource(options.ConnectionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token
        );

        try
        {
            client = new TcpClient();
            await client
                .ConnectAsync(options.Server, options.Port, linked.Token)
                .ConfigureAwait(false);
            stream = client.GetStream();

            var reader = new PacketReader(stream);
            byte[] handshakePayload = await reader
                .ReadPayloadAsync(linked.Token)
                .ConfigureAwait(false);
            ServerHandshake handshake = ServerHandshake.Parse(handshakePayload);

            byte[] responsePayload = HandshakeResponseBuilder.Build(options, handshake);
            var writer = new PacketWriter(stream);
            writer.ResetSequence(reader.SequenceId);
            await writer.WritePayloadAsync(responsePayload, linked.Token).ConfigureAwait(false);

            reader.ResetSequence(writer.SequenceId);
            byte[] authResultPayload = await reader
                .ReadPayloadAsync(linked.Token)
                .ConfigureAwait(false);
            AuthenticationResult.Read(authResultPayload, handshake.ConnectionId);

            var physical = new DotRocksPhysicalConnection(client, stream, handshake.ServerVersion);
            client = null;
            stream = null;
            return physical;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DotRocksException)
        {
            throw;
        }
        catch (MalformedPacketException ex)
        {
            throw new DotRocksException("StarRocks returned malformed protocol bytes.", ex);
        }
        catch (SocketException ex)
        {
            throw new DotRocksException(
                "Could not connect to the StarRocks server.",
                serverErrorCode: null,
                sqlState: null,
                isTransient: true,
                connectionId: null,
                innerException: ex
            );
        }
        catch (IOException ex)
        {
            throw new DotRocksException(
                "I/O failed while opening the StarRocks connection.",
                serverErrorCode: null,
                sqlState: null,
                isTransient: true,
                connectionId: null,
                innerException: ex
            );
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            client?.Dispose();
        }
    }

    public async ValueTask<QueryResult> ExecuteQueryAsync(
        string commandText,
        CancellationToken cancellationToken
    )
    {
        if (_isDisposed)
        {
            throw new InvalidOperationException("The physical StarRocks connection is closed.");
        }

        try
        {
            byte[] payload = QueryCommandBuilder.Build(commandText);
            var writer = new PacketWriter(_stream);
            writer.ResetSequence();
            await writer.WritePayloadAsync(payload, cancellationToken).ConfigureAwait(false);

            var reader = new PacketReader(_stream);
            reader.ResetSequence(1);
            byte[] firstPayload = await reader
                .ReadPayloadAsync(cancellationToken)
                .ConfigureAwait(false);
            return await TextResultParser
                .ReadAsync(firstPayload, reader, connectionId: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MarkBroken();
            throw;
        }
        catch (IOException ex)
        {
            MarkBroken();
            throw new DotRocksException(
                "I/O failed while executing the StarRocks command.",
                serverErrorCode: null,
                sqlState: null,
                isTransient: true,
                connectionId: null,
                innerException: ex
            );
        }
        catch (ObjectDisposedException ex)
        {
            MarkBroken();
            throw new DotRocksException(
                "The StarRocks connection was closed while executing a command.",
                serverErrorCode: null,
                sqlState: null,
                isTransient: true,
                connectionId: null,
                innerException: ex
            );
        }
    }

    public void MarkBroken() => _isBroken = true;

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _stream.Dispose();
        _client.Dispose();
    }
}
