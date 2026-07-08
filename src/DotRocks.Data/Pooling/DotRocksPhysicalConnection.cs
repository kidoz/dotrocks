using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DotRocks.Data;
using DotRocks.Data.Protocol.Commands;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Pooling;

internal sealed class DotRocksPhysicalConnection : IDisposable
{
    private const int MaxCachedPreparedStatements = 64;

    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly long _createdTimestamp;
    private readonly TimeSpan _maxLifetime;

    // Server-prepared statements are session-scoped, so they stay valid for the life of this
    // physical connection and can be reused across pool leases. Cache by SQL text with a bounded
    // FIFO eviction. Only one command is active per physical connection, so no synchronization is
    // needed.
    private readonly Dictionary<string, StatementPrepareResult> _preparedStatements = new(
        StringComparer.Ordinal
    );
    private readonly Queue<string> _preparedStatementOrder = new();

    private volatile bool _isDisposed;
    private volatile bool _isBroken;
    private volatile bool _sessionMayBeDirty;

    private DotRocksPhysicalConnection(
        TcpClient client,
        Stream stream,
        string serverVersion,
        TimeSpan maxLifetime
    )
    {
        _client = client;
        _stream = stream;
        ServerVersion = serverVersion;
        _maxLifetime = maxLifetime;
        _createdTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// The MySQL-compatibility version string from the handshake (for example <c>8.0.33</c>). This
    /// is the honest ADO.NET <c>ServerVersion</c> identity; it is not the StarRocks version and must
    /// not be used for capability gating — query <see cref="QueryServerVersionAsync"/> for that.
    /// </summary>
    public string ServerVersion { get; }

    /// <summary>
    /// True when the connection's protocol state is unusable: disposed, or broken mid-command
    /// (I/O failure, malformed packet, cancellation). Distinct from <see cref="IsReusable"/>,
    /// which additionally applies pool-reuse policy (session dirtiness, lifetime); a healthy
    /// connection that merely received a server error packet is not broken.
    /// </summary>
    public bool IsBroken => _isDisposed || _isBroken;

    // A connection whose session state may have been mutated (USE / SET) is not reused because
    // session reset is not verified. Reusing it could leak the current database or session
    // variables into the next lease, so the pool discards it on return.
    // A connection past its (jittered) maximum lifetime is likewise retired rather than reused.
    public bool IsReusable =>
        !_isDisposed
        && !_isBroken
        && !_sessionMayBeDirty
        && !HasExceededLifetime
        && _client.Connected
        && IsSocketAlive();

    private bool HasExceededLifetime =>
        _maxLifetime > TimeSpan.Zero && Stopwatch.GetElapsedTime(_createdTimestamp) >= _maxLifetime;

    // TcpClient.Connected only reflects the last I/O. A correctly drained idle pooled connection
    // has nothing to read, so anything readable means it must not be reused: a readable socket is
    // either closed (FIN/RST, Available == 0) or carries leftover/unsolicited bytes that would
    // desynchronize the protocol on the next command.
    private bool IsSocketAlive()
    {
        try
        {
            return !_client.Client.Poll(0, SelectMode.SelectRead);
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

            if (ShouldNegotiateTls(options, handshake))
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

            var physical = new DotRocksPhysicalConnection(
                client,
                stream,
                handshake.ServerVersion,
                ComputeJitteredLifetime(options.ConnectionLifetime)
            );
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
            // A peer that accepts the socket then drops it during the handshake (RST or FIN)
            // surfaces as an end-of-stream/IO failure wrapped in a malformed-packet error. That is
            // a transient open failure (e.g. a draining/restarting FE) and is safe to retry, unlike
            // genuinely malformed handshake bytes, which stay non-transient.
            throw new DotRocksException(
                "StarRocks returned malformed protocol bytes.",
                serverErrorCode: null,
                sqlState: null,
                isTransient: ex.InnerException is EndOfStreamException or IOException,
                connectionId: null,
                innerException: ex
            );
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
            // TLS negotiation failures are dominated by certificate name/chain/revocation problems,
            // which are configuration or security faults, not transient FE outages. Classify them
            // non-transient so the connection-open retry loop does not re-handshake on a setup error.
            throw new DotRocksException(
                "TLS negotiation failed while opening the StarRocks connection.",
                serverErrorCode: null,
                sqlState: null,
                isTransient: false,
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

    // Decide whether to perform the TLS upgrade for this handshake. Required always upgrades and
    // lets BuildSslRequest fail loudly when the server cannot negotiate TLS. Preferred upgrades
    // only when the server advertises TLS, otherwise it continues in plaintext (opportunistic).
    private static bool ShouldNegotiateTls(
        DotRocksConnectionOptions options,
        ServerHandshake handshake
    ) =>
        options.SslMode switch
        {
            DotRocksSslMode.Required => true,
            DotRocksSslMode.Preferred => handshake.Capabilities.HasFlag(CapabilityFlags.Ssl),
            _ => false,
        };

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

    public ValueTask<QueryResult> ExecuteQueryAsync(
        string commandText,
        CancellationToken cancellationToken
    )
    {
        MarkSessionDirtyIfMutating(commandText);
        return ExecuteExchangeAsync(
            QueryCommandBuilder.Build(commandText),
            "I/O failed while executing the StarRocks command.",
            static (firstPayload, reader, ct) =>
                TextResultParser.ReadAsync(firstPayload, reader, connectionId: null, ct),
            cancellationToken
        );
    }

    /// <summary>
    /// Sends one command payload and parses its response with <paramref name="parseResponse"/>.
    /// Owns the shared exchange preamble (write the request, continue the packet sequence into the
    /// response) and the single exception-translation ladder; any failure that can leave the wire
    /// off a clean packet boundary marks the connection broken.
    /// </summary>
    private async ValueTask<T> ExecuteExchangeAsync<T>(
        byte[] payload,
        string ioFailureMessage,
        Func<byte[], PacketReader, CancellationToken, ValueTask<T>> parseResponse,
        CancellationToken cancellationToken
    )
    {
        if (_isDisposed)
        {
            throw new InvalidOperationException("The physical StarRocks connection is closed.");
        }

        try
        {
            var writer = new PacketWriter(_stream);
            writer.ResetSequence();
            await writer.WritePayloadAsync(payload, cancellationToken).ConfigureAwait(false);

            var reader = new PacketReader(_stream);
            // A command payload of 16 MiB or more spans several request packets, so the response
            // continues from the writer's next sequence id, not a fixed 1.
            reader.ResetSequence(writer.SequenceId);
            byte[] firstPayload = await reader
                .ReadPayloadAsync(cancellationToken)
                .ConfigureAwait(false);
            return await parseResponse(firstPayload, reader, cancellationToken)
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
                ioFailureMessage,
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

    /// <summary>
    /// Sends <c>COM_STMT_PREPARE</c> for the given SQL and parses the prepare response, consuming the
    /// parameter and column definition packets (and their trailing EOF packets, since DotRocks does
    /// not negotiate <c>CLIENT_DEPRECATE_EOF</c>). Returns the server statement id and counts.
    /// </summary>
    public ValueTask<StatementPrepareResult> PrepareAsync(
        string commandText,
        CancellationToken cancellationToken
    )
    {
        // A prepared statement can mutate session state just like a text command (for example
        // "SET @tenant := ?"), so classify it here too; otherwise the mutation leaks into the next
        // lease of the pooled connection.
        MarkSessionDirtyIfMutating(commandText);
        return ExecuteExchangeAsync(
            StatementCommandBuilder.BuildPrepare(commandText),
            "I/O failed while preparing the StarRocks statement.",
            static (firstPayload, reader, ct) =>
                ParsePrepareResponseAsync(firstPayload, reader, ct),
            cancellationToken
        );
    }

    private static async ValueTask<StatementPrepareResult> ParsePrepareResponseAsync(
        byte[] firstPayload,
        PacketReader reader,
        CancellationToken cancellationToken
    )
    {
        if (ResultPacket.IsError(firstPayload))
        {
            throw ResultPacket.ReadError(firstPayload, connectionId: null);
        }

        var protocolReader = new ProtocolReader(firstPayload);
        byte status = protocolReader.ReadByte();
        if (status != 0x00)
        {
            throw new MalformedPacketException(
                $"Unexpected COM_STMT_PREPARE response status 0x{status:X2}."
            );
        }

        uint statementId = (uint)protocolReader.ReadFixedInteger(4);

        // The counts are 2-byte wire fields, so each is inherently bounded to 65535; the
        // definition-consume loops below also honor the cancellation token (command timeout) on
        // every packet read, so a hostile count cannot cause an unbounded stall.
        int columnCount = (int)protocolReader.ReadFixedInteger(2);
        int parameterCount = (int)protocolReader.ReadFixedInteger(2);

        // Consume the parameter-definition packets (+EOF) and column-definition packets (+EOF)
        // so the connection is left at a clean packet boundary for the next command.
        await ConsumeDefinitionBlockAsync(reader, parameterCount, cancellationToken)
            .ConfigureAwait(false);
        await ConsumeDefinitionBlockAsync(reader, columnCount, cancellationToken)
            .ConfigureAwait(false);

        return new StatementPrepareResult(statementId, parameterCount, columnCount);
    }

    /// <summary>
    /// Returns a prepared statement for the given SQL, reusing a cached one for this connection when
    /// possible (since prepared statements are session-scoped) and otherwise preparing it and adding
    /// it to the bounded cache.
    /// </summary>
    public async ValueTask<StatementPrepareResult> PrepareCachedAsync(
        string commandText,
        CancellationToken cancellationToken
    )
    {
        if (_preparedStatements.TryGetValue(commandText, out StatementPrepareResult cached))
        {
            return cached;
        }

        StatementPrepareResult prepared = await PrepareAsync(commandText, cancellationToken)
            .ConfigureAwait(false);

        if (_preparedStatements.Count >= MaxCachedPreparedStatements)
        {
            string evictedKey = _preparedStatementOrder.Dequeue();
            if (_preparedStatements.Remove(evictedKey, out StatementPrepareResult evicted))
            {
                await ClosePreparedStatementAsync(evicted.StatementId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _preparedStatements[commandText] = prepared;
        _preparedStatementOrder.Enqueue(commandText);
        return prepared;
    }

    /// <summary>
    /// Sends <c>COM_STMT_EXECUTE</c> for a prepared statement with the given positional parameter
    /// values and reads the binary result set into a buffered <see cref="QueryResult"/>. The whole
    /// result is buffered so the statement can be closed immediately after.
    /// </summary>
    public ValueTask<QueryResult> ExecutePreparedAsync(
        uint statementId,
        IReadOnlyList<object?> parameterValues,
        CancellationToken cancellationToken
    ) =>
        ExecuteExchangeAsync(
            BinaryParameterEncoder.BuildExecute(statementId, parameterValues),
            "I/O failed while executing the prepared StarRocks statement.",
            static (firstPayload, reader, ct) =>
                TextResultParser.ReadBinaryAsync(firstPayload, reader, connectionId: null, ct),
            cancellationToken
        );

    /// <summary>Sends <c>COM_STMT_CLOSE</c> for the given statement id. The server sends no reply.</summary>
    public async ValueTask ClosePreparedStatementAsync(
        uint statementId,
        CancellationToken cancellationToken
    )
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            byte[] payload = StatementCommandBuilder.BuildClose(statementId);
            var writer = new PacketWriter(_stream);
            writer.ResetSequence();
            await writer.WritePayloadAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancellation that lands mid-write can leave a partial COM_STMT_CLOSE frame on the
            // wire; the connection is no longer at a packet boundary and must not be reused.
            MarkBroken();
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            MarkBroken();
        }
    }

    private static async ValueTask ConsumeDefinitionBlockAsync(
        PacketReader reader,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (count == 0)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            await reader.ReadPayloadAsync(cancellationToken).ConfigureAwait(false);
        }

        // Without CLIENT_DEPRECATE_EOF the definition block is terminated by an EOF packet.
        byte[] terminator = await reader.ReadPayloadAsync(cancellationToken).ConfigureAwait(false);
        if (!ResultPacket.IsEndOfResultSet(terminator))
        {
            throw new MalformedPacketException(
                "Expected an EOF packet after a prepared-statement definition block."
            );
        }
    }

    /// <summary>
    /// Queries the authoritative StarRocks version via <c>SELECT current_version()</c>. The MySQL
    /// handshake only carries a bare compatibility string (e.g. <c>8.0.33</c>), so this is the
    /// source used for capability gating. Best-effort: any server/protocol failure (an older
    /// server, a restricted role) yields <see cref="DotRocksServerVersion.Unknown"/> so callers gate
    /// conservatively rather than failing the operation.
    /// </summary>
    public async ValueTask<DotRocksServerVersion> QueryServerVersionAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            QueryResult result = await ExecuteQueryAsync(
                    "SELECT current_version()",
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result.Rows.Count > 0 && result.Rows[0].Length > 0)
            {
                return DotRocksServerVersion.ParseCurrentVersion(result.Rows[0][0]?.ToString());
            }
        }
        catch (DotRocksException)
        {
            // A server error (e.g. the function is unavailable) leaves the connection usable; gate
            // conservatively rather than failing.
        }

        return DotRocksServerVersion.Unknown;
    }

    public ValueTask<StreamingQueryResult> ExecuteQueryStreamingAsync(
        string commandText,
        CancellationToken cancellationToken
    )
    {
        MarkSessionDirtyIfMutating(commandText);
        return ExecuteExchangeAsync(
            QueryCommandBuilder.Build(commandText),
            "I/O failed while executing the StarRocks command.",
            static (firstPayload, reader, ct) =>
                TextResultParser.ReadStreamingAsync(firstPayload, reader, connectionId: null, ct),
            cancellationToken
        );
    }

    public void MarkBroken() => _isBroken = true;

    // Discard connections after USE / SET because per-lease session reset is not verified.
    private void MarkSessionDirtyIfMutating(string commandText)
    {
        if (SqlStatementClassifier.IsSessionMutating(commandText))
        {
            _sessionMayBeDirty = true;
        }
    }

    // Spread connection retirement so connections opened together do not all expire at the same
    // instant (a reconnect storm). Each connection lives 90-100% of the configured lifetime; zero
    // means an unbounded lifetime.
    [SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification = "Lifetime jitter only spreads reconnect timing; it is not used for any security decision."
    )]
    private static TimeSpan ComputeJitteredLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        double jitterMilliseconds = Random.Shared.NextDouble() * lifetime.TotalMilliseconds * 0.1;
        return lifetime - TimeSpan.FromMilliseconds(jitterMilliseconds);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // The session ends when the socket closes, so the server drops every prepared statement;
        // release the cache without sending per-statement COM_STMT_CLOSE packets.
        _preparedStatements.Clear();
        _preparedStatementOrder.Clear();
        _stream.Dispose();
        _client.Dispose();
    }
}
