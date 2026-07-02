using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotRocks.Data.Authentication;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.TestInfrastructure;

/// <summary>
/// A scripted in-process StarRocks server. Each expected inbound connection is served by the
/// handler at the matching index, and any unexpected connection fails the test loudly. Compose
/// handlers from the static connection scripts below and the payloads in
/// <see cref="StarRocksPacketFactory"/>.
/// </summary>
internal sealed class FakeStarRocksServer : IDisposable
{
    /// <summary>The user name the fake server authenticates.</summary>
    public const string UserName = "alice";

    /// <summary>
    /// The password carried by <see cref="ConnectionString"/>; tests assert it never leaks into
    /// exceptions or public connection-string surfaces.
    /// </summary>
    public const string Secret = "fake-server-secret";

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

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>A plaintext connection string targeting this server with the fake credentials.</summary>
    public string ConnectionString =>
        $"Server=127.0.0.1;Port={Port};User ID={UserName};Password={Secret};Connection Timeout=5";

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

    /// <summary>
    /// Sends the greeting, reads the client handshake response without verifying it, and replies
    /// OK, leaving the connection authenticated and idle.
    /// </summary>
    public static async Task CompleteAuthenticationAsync(NetworkStream stream)
    {
        var writer = new PacketWriter(stream);
        await writer
            .WritePayloadAsync(
                StarRocksPacketFactory.Handshake(MySqlNativePassword.PluginName),
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
            .WritePayloadAsync(StarRocksPacketFactory.Ok(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Sends the greeting and verifies the client's mysql_native_password scramble against
    /// <see cref="Secret"/>, replying OK only when the user name and scramble match.
    /// </summary>
    public static async Task CompleteAuthenticationVerifyingScrambleAsync(NetworkStream stream)
    {
        var writer = new PacketWriter(stream);
        await writer
            .WritePayloadAsync(
                StarRocksPacketFactory.Handshake(MySqlNativePassword.PluginName),
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
            ? StarRocksPacketFactory.Ok()
            : StarRocksPacketFactory.Error("Access denied");
        await writer
            .WritePayloadAsync(reply, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>Reads one command packet, replies OK, and returns the command's SQL text.</summary>
    public static async Task<string> ReadCommandAndReplyOkAsync(NetworkStream stream)
    {
        var reader = new PacketReader(stream);
        reader.ResetSequence(0);
        byte[] payload = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var writer = new PacketWriter(stream);
        writer.ResetSequence(reader.SequenceId);
        await writer
            .WritePayloadAsync(StarRocksPacketFactory.Ok(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // COM_QUERY payload is a 0x03 command byte followed by the SQL text.
        return Encoding.UTF8.GetString(payload.AsSpan(1));
    }

    /// <summary>
    /// Reads one command packet, replies with an ERR packet, and returns the command's SQL text.
    /// </summary>
    public static async Task<string> ReadCommandAndReplyErrorAsync(NetworkStream stream)
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
                StarRocksPacketFactory.Error("Unknown column 'broken'"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        return Encoding.UTF8.GetString(payload.AsSpan(1));
    }

    /// <summary>
    /// Reads one command packet, replies with a single text column named "value" containing the
    /// given rows, and returns the command's SQL text.
    /// </summary>
    public static async Task<string> ReadCommandAndReplyResultSetAsync(
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
        await writer.WritePayloadAsync(new byte[] { 0x01 }, ct).ConfigureAwait(true); // column count
        await writer
            .WritePayloadAsync(StarRocksPacketFactory.ColumnDefinition("value"), ct)
            .ConfigureAwait(true);
        await writer.WritePayloadAsync(StarRocksPacketFactory.Eof(), ct).ConfigureAwait(true);
        foreach (string row in rows)
        {
            await writer
                .WritePayloadAsync(StarRocksPacketFactory.TextRow(row), ct)
                .ConfigureAwait(true);
        }

        await writer.WritePayloadAsync(StarRocksPacketFactory.Eof(), ct).ConfigureAwait(true);
        return Encoding.UTF8.GetString(payload.AsSpan(1));
    }

    /// <summary>Authenticates the connection and then keeps it idle briefly.</summary>
    public static async Task HandleOpenOnlyConnectionAsync(NetworkStream stream)
    {
        await CompleteAuthenticationAsync(stream).ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Advertises TLS, upgrades the connection with a self-signed certificate, completes
    /// authentication over TLS, and then keeps the connection idle briefly.
    /// </summary>
    public static async Task HandleTlsOpenOnlyConnectionAsync(NetworkStream stream)
    {
        var writer = new PacketWriter(stream);
        await writer
            .WritePayloadAsync(
                StarRocksPacketFactory.Handshake(MySqlNativePassword.PluginName, supportsTls: true),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        var reader = new PacketReader(stream);
        reader.ResetSequence(writer.SequenceId);
        byte[] sslRequestPayload = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        AssertSslRequest(sslRequestPayload);
        byte authSequence = reader.SequenceId;

        using X509Certificate2 certificate = CreateSelfSignedCertificate();
        using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.None,
                    ClientCertificateRequired = false,
                },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        reader = new PacketReader(tls);
        reader.ResetSequence(authSequence);
        _ = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        writer = new PacketWriter(tls);
        writer.ResetSequence(reader.SequenceId);
        await writer
            .WritePayloadAsync(StarRocksPacketFactory.Ok(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Advertises TLS and presents a self-signed certificate, expecting the client to reject it
    /// and abort the handshake.
    /// </summary>
    public static async Task HandleTlsRejectingConnectionAsync(NetworkStream stream)
    {
        var writer = new PacketWriter(stream);
        await writer
            .WritePayloadAsync(
                StarRocksPacketFactory.Handshake(MySqlNativePassword.PluginName, supportsTls: true),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var reader = new PacketReader(stream);
        reader.ResetSequence(writer.SequenceId);
        _ = await reader
            .ReadPayloadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        using X509Certificate2 certificate = CreateSelfSignedCertificate();
        using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
        try
        {
            await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        EnabledSslProtocols = SslProtocols.None,
                        ClientCertificateRequired = false,
                    },
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        }
        catch (AuthenticationException)
        {
            // Expected: the client rejects the untrusted certificate and aborts the handshake.
        }
        catch (IOException)
        {
            // Expected: the client closes the socket once it rejects the certificate.
        }
    }

    private static bool AuthResponseMatchesSecret(byte[] handshakeResponse)
    {
        // The 20-byte challenge is part 1 plus part 2 without its trailing NUL.
        byte[] part1 = StarRocksPacketFactory.AuthPart1;
        byte[] part2 = StarRocksPacketFactory.AuthPart2;
        byte[] challenge = new byte[part1.Length + part2.Length - 1];
        part1.CopyTo(challenge, 0);
        part2.AsSpan(0, part2.Length - 1).CopyTo(challenge.AsSpan(part1.Length));
        byte[] expected = MySqlNativePassword.CreateAuthenticationResponse(Secret, challenge);

        var reader = new ProtocolReader(handshakeResponse);
        _ = reader.ReadFixedInteger(4); // capabilities
        _ = reader.ReadFixedInteger(4); // max packet size
        _ = reader.ReadByte(); // character set
        _ = reader.ReadBytes(23); // reserved
        string user = reader.ReadNullTerminatedString(Encoding.UTF8);
        ReadOnlySpan<byte> authResponse = reader.ReadLengthEncodedBytes(out _);
        return user == UserName && authResponse.SequenceEqual(expected);
    }

    private static void AssertSslRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtocolReader(payload);
        var capabilities = (CapabilityFlags)reader.ReadFixedInteger(4);
        Assert.True(capabilities.HasFlag(CapabilityFlags.Ssl));
        Assert.True(capabilities.HasFlag(CapabilityFlags.Protocol41));
        Assert.True(capabilities.HasFlag(CapabilityFlags.SecureConnection));
        Assert.Equal(0UL, reader.ReadFixedInteger(4));
        Assert.Equal(StarRocksPacketFactory.Utf8GeneralCiCollation, reader.ReadByte());
        reader.ReadBytes(23);
        Assert.True(reader.IsAtEnd);
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=127.0.0.1",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1)
        );
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
