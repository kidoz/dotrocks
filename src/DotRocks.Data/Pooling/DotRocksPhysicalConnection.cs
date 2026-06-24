using System.Diagnostics;
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
    private readonly long _createdTimestamp;
    private readonly TimeSpan _maxLifetime;
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

    // A connection whose session state may have been mutated (USE / SET) is not reused: DotRocks
    // does not yet perform a verified session reset, so reusing it could leak the current database
    // or session variables into the next lease. Such connections are discarded on return instead.
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

    public async ValueTask<QueryResult> ExecuteQueryAsync(
        string commandText,
        CancellationToken cancellationToken
    )
    {
        if (_isDisposed)
        {
            throw new InvalidOperationException("The physical StarRocks connection is closed.");
        }

        MarkSessionDirtyIfMutating(commandText);

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

    public async ValueTask<StreamingQueryResult> ExecuteQueryStreamingAsync(
        string commandText,
        CancellationToken cancellationToken
    )
    {
        if (_isDisposed)
        {
            throw new InvalidOperationException("The physical StarRocks connection is closed.");
        }

        MarkSessionDirtyIfMutating(commandText);

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

    // Conservative session-state guard: DotRocks does not yet perform a verified per-lease session
    // reset, so a connection that ran a statement which can change the current database or session
    // variables (USE / SET) must not be returned to the pool for reuse.
    private void MarkSessionDirtyIfMutating(string commandText)
    {
        if (IsSessionMutatingStatement(commandText))
        {
            _sessionMayBeDirty = true;
        }
    }

    // Detection is intentionally conservative (it errs toward discarding): it skips leading
    // whitespace and SQL comments, then flags a statement whose leading keyword is USE or SET.
    internal static bool IsSessionMutatingStatement(string commandText)
    {
        if (string.IsNullOrEmpty(commandText))
        {
            return false;
        }

        ReadOnlySpan<char> sql = commandText.AsSpan();
        int index = SkipLeadingTrivia(sql);
        return MatchesKeyword(sql, index, "USE") || MatchesKeyword(sql, index, "SET");
    }

    private static int SkipLeadingTrivia(ReadOnlySpan<char> sql)
    {
        int index = 0;
        while (index < sql.Length)
        {
            char current = sql[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            // Line comments: "-- ..." and "# ..." run to the end of the line.
            if (
                current == '#'
                || (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            )
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            // Block comment: "/* ... */".
            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < sql.Length && !(sql[index] == '*' && sql[index + 1] == '/'))
                {
                    index++;
                }

                index = Math.Min(index + 2, sql.Length);
                continue;
            }

            break;
        }

        return index;
    }

    private static bool MatchesKeyword(ReadOnlySpan<char> sql, int index, string keyword)
    {
        if (index + keyword.Length > sql.Length)
        {
            return false;
        }

        if (!sql.Slice(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Require a word boundary so identifiers like USER or SETTINGS do not match.
        int next = index + keyword.Length;
        return next >= sql.Length || !IsIdentifierPart(sql[next]);
    }

    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$';

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
        _stream.Dispose();
        _client.Dispose();
    }
}
