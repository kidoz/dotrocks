using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using DotRocks.Data.Loading;
using DotRocks.Data.Protocol.Commands;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data;

/// <summary>
/// Represents a connection to a StarRocks FE query endpoint.
/// </summary>
public sealed class DotRocksConnection : DbConnection
{
    private DotRocksConnectionOptions _options;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private string _serverVersion = string.Empty;
    private ConnectionState _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksConnection"/> class.
    /// </summary>
    public DotRocksConnection()
    {
        _options = DotRocksConnectionOptions.Default;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksConnection"/> class.
    /// </summary>
    /// <param name="connectionString">The connection string to use.</param>
    public DotRocksConnection(string connectionString)
        : this()
    {
        ConnectionString = connectionString;
    }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString
    {
        get => _options.ConnectionString;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException(
                    "The connection string cannot be changed while the connection is open."
                );
            }

            _options = DotRocksConnectionOptions.Parse(value);
        }
    }

    /// <inheritdoc />
    public override string Database => _options.Database;

    /// <inheritdoc />
    public override string DataSource => _options.Server;

    /// <inheritdoc />
    public override string ServerVersion => _serverVersion;

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException(
            "Changing the database on an open DotRocks connection is not supported yet."
        );

    /// <inheritdoc />
    public override void Close()
    {
        CloseCore();
    }

    private void CloseCore()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        _serverVersion = string.Empty;
        _state = ConnectionState.Closed;
    }

    /// <inheritdoc />
    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The connection is already open.");
        }

        _state = ConnectionState.Connecting;
        using var timeout = new CancellationTokenSource(_options.ConnectionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token
        );

        try
        {
            var client = new TcpClient();
            await client
                .ConnectAsync(_options.Server, _options.Port, linked.Token)
                .ConfigureAwait(false);
            NetworkStream stream = client.GetStream();

            var reader = new PacketReader(stream);
            byte[] handshakePayload = await reader
                .ReadPayloadAsync(linked.Token)
                .ConfigureAwait(false);
            ServerHandshake handshake = ServerHandshake.Parse(handshakePayload);

            byte[] responsePayload = HandshakeResponseBuilder.Build(_options, handshake);
            var writer = new PacketWriter(stream);
            writer.ResetSequence(reader.SequenceId);
            await writer.WritePayloadAsync(responsePayload, linked.Token).ConfigureAwait(false);

            reader.ResetSequence(writer.SequenceId);
            byte[] authResultPayload = await reader
                .ReadPayloadAsync(linked.Token)
                .ConfigureAwait(false);
            AuthenticationResult.Read(authResultPayload, handshake.ConnectionId);

            _client = client;
            _stream = stream;
            _serverVersion = handshake.ServerVersion;
            _state = ConnectionState.Open;
        }
        catch (OperationCanceledException)
        {
            CloseCore();
            throw;
        }
        catch (DotRocksException)
        {
            CloseCore();
            throw;
        }
        catch (MalformedPacketException ex)
        {
            CloseCore();
            throw new DotRocksException("StarRocks returned malformed protocol bytes.", ex);
        }
        catch (SocketException ex)
        {
            CloseCore();
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
            CloseCore();
            throw new DotRocksException(
                "I/O failed while opening the StarRocks connection.",
                serverErrorCode: null,
                sqlState: null,
                isTransient: true,
                connectionId: null,
                innerException: ex
            );
        }
    }

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("Transactions are not implemented yet.");

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new DotRocksCommand(this);

    internal async ValueTask<QueryResult> ExecuteQueryAsync(
        string commandText,
        CancellationToken cancellationToken
    )
    {
        if (_state != ConnectionState.Open || _stream is null)
        {
            throw new InvalidOperationException("The connection is not open.");
        }

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

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }
}
