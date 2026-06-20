using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DotRocks.Data;
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
    private readonly Stream _stream;
    private volatile bool _isDisposed;
    private volatile bool _isBroken;

    private DotRocksPhysicalConnection(TcpClient client, Stream stream, string serverVersion)
    {
        _client = client;
        _stream = stream;
        ServerVersion = serverVersion;
    }

    public string ServerVersion { get; }

    public bool IsReusable => !_isDisposed && !_isBroken && _client.Connected && IsSocketAlive();

    // TcpClient.Connected only reflects the last I/O. Poll detects a peer that closed the
    // connection while it was idle in the pool (server restart, idle kill): a readable socket
    // with no available bytes means a FIN was received, so the connection must not be reused.
    private bool IsSocketAlive()
    {
        try
        {
            Socket socket = _client.Client;
            return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public static async ValueTask<DotRocksPhysicalConnection> OpenAsync(
        DotRocksConnectionOptions options,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        TcpClient? client = null;
        Stream? stream = null;
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

            if (options.SslMode == DotRocksSslMode.Required)
            {
                var sslRequestWriter = new PacketWriter(stream);
                sslRequestWriter.ResetSequence(reader.SequenceId);
                byte[] sslRequestPayload = HandshakeResponseBuilder.BuildSslRequest(
                    options,
                    handshake
                );
                await sslRequestWriter
                    .WritePayloadAsync(sslRequestPayload, linked.Token)
                    .ConfigureAwait(false);

                stream = await UpgradeToTlsAsync(options, stream, linked.Token)
                    .ConfigureAwait(false);
                reader = new PacketReader(stream);
                reader.ResetSequence(sslRequestWriter.SequenceId);
            }

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
        catch (AuthenticationException ex)
        {
            throw new DotRocksException(
                "TLS negotiation failed while opening the StarRocks connection.",
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

    [SuppressMessage(
        "Security",
        "CA5359:Do Not Disable Certificate Validation",
        Justification = "Only disabled when callers explicitly set Trust Server Certificate=true for controlled environments."
    )]
    private static async ValueTask<Stream> UpgradeToTlsAsync(
        DotRocksConnectionOptions options,
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        var sslStream = new SslStream(
            stream,
            false,
            options.TrustServerCertificate ? TrustAnyServerCertificate : null
        );
        try
        {
            await sslStream
                .AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = options.Server,
                        EnabledSslProtocols = SslProtocols.None,
                        // Defaults to Offline (cached CRLs only) to avoid a blocking OCSP/CRL
                        // network fetch during the handshake; configurable via Ssl Revocation
                        // Check. Certificate chain and name validation remain enforced unless
                        // Trust Server Certificate is set.
                        CertificateRevocationCheckMode = options.SslRevocationMode,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            return sslStream;
        }
        catch
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool TrustAnyServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors
    ) => true;

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
        catch (MalformedPacketException ex)
        {
            MarkBroken();
            throw new DotRocksException("StarRocks returned malformed protocol bytes.", ex);
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

    public async ValueTask<StreamingQueryResult> ExecuteQueryStreamingAsync(
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
                .ReadStreamingAsync(firstPayload, reader, connectionId: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MarkBroken();
            throw;
        }
        catch (MalformedPacketException ex)
        {
            MarkBroken();
            throw new DotRocksException("StarRocks returned malformed protocol bytes.", ex);
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
