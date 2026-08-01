using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Flight.Sql.Client;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace DotRocks.FlightSql;

/// <summary>
/// Owns one gRPC channel to a Flight endpoint together with the server session established over it.
/// </summary>
/// <remarks>
/// StarRocks creates a frontend session for every authenticated call, so repeating the Basic
/// credentials on each RPC leaks one server connection per RPC until it times out. The credentials
/// are therefore exchanged once during the Flight handshake for a bearer token that every later
/// call reuses, and the session is released with the Flight SQL <c>CloseSession</c> action on
/// disposal. Servers that do not implement the handshake keep the per-call Basic behavior.
/// </remarks>
internal sealed class FlightSqlConnection : IAsyncDisposable
{
    private const string AuthorizationHeader = "authorization";
    private const string CloseSessionAction = "CloseSession";
    private static readonly TimeSpan s_closeSessionTimeout = TimeSpan.FromSeconds(5);

    private readonly GrpcChannel _channel;
    private readonly SemaphoreSlim _authenticationGate = new(1, 1);
    private readonly string _basicAuthorization;
    private readonly TimeSpan _commandTimeout;
    private string? _sessionAuthorization;
    private bool _authenticated;

    public FlightSqlConnection(
        Uri address,
        string userName,
        string password,
        TimeSpan commandTimeout
    )
    {
        _channel = GrpcChannel.ForAddress(
            address,
            new GrpcChannelOptions
            {
                HttpHandler = CreateHttpHandler(),
                DisposeHttpClient = true,

                // Cancelled calls must surface as OperationCanceledException so that ADO.NET
                // consumers can distinguish cancellation from a transport failure.
                ThrowOperationCanceledOnCancellation = true,
            }
        );
        Client = new FlightClient(_channel);
        SqlClient = new FlightSqlClient(Client);
        _basicAuthorization = CreateBasicAuthorizationValue(userName, password);
        _commandTimeout = commandTimeout;
    }

    public FlightClient Client { get; }

    public FlightSqlClient SqlClient { get; }

    /// <summary>
    /// Returns the call headers for this connection, authenticating the session on first use.
    /// </summary>
    public async ValueTask<Metadata> CreateHeadersAsync(CancellationToken cancellationToken)
    {
        string authorization = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        return new Metadata { { AuthorizationHeader, authorization } };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _authenticationGate.Dispose();
            _channel.Dispose();
        }
    }

    internal static SocketsHttpHandler CreateHttpHandler() =>
        new() { SslOptions = { CertificateRevocationCheckMode = X509RevocationMode.Offline } };

    internal static string CreateBasicAuthorizationValue(string userName, string password)
    {
        string credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(userName + ":" + password)
        );
        return "Basic " + credentials;
    }

    private async ValueTask<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _authenticated))
        {
            return _sessionAuthorization ?? _basicAuthorization;
        }

        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_authenticated)
            {
                _sessionAuthorization = await HandshakeAsync(cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref _authenticated, true);
            }
        }
        finally
        {
            _authenticationGate.Release();
        }

        return _sessionAuthorization ?? _basicAuthorization;
    }

    private async Task<string?> HandshakeAsync(CancellationToken cancellationToken)
    {
        using AsyncDuplexStreamingCall<FlightHandshakeRequest, FlightHandshakeResponse> call =
            Client.Handshake(
                new Metadata { { AuthorizationHeader, _basicAuthorization } },
                CreateDeadline(_commandTimeout),
                cancellationToken
            );
        try
        {
            await WriteHandshakeRequestAsync(call.RequestStream).ConfigureAwait(false);
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            Metadata headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                // The token travels in the call metadata; StarRocks sends no handshake payload.
            }

            return FindAuthorization(headers) ?? FindAuthorization(call.GetTrailers());
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Unimplemented)
        {
            // Flight servers may omit the handshake; those keep per-call Basic authentication.
            return null;
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2016:Forward the CancellationToken parameter to methods",
        Justification = "Grpc.Net.Client rejects cancellable stream writes; the call itself carries the token and the deadline."
    )]
    private static Task WriteHandshakeRequestAsync(
        IClientStreamWriter<FlightHandshakeRequest> requestStream
    ) => requestStream.WriteAsync(new FlightHandshakeRequest(ByteString.Empty));

    private async Task CloseSessionAsync()
    {
        string? authorization = Interlocked.Exchange(ref _sessionAuthorization, null);
        if (authorization is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(s_closeSessionTimeout);

            // The Flight SQL CloseSession action carries no request payload for StarRocks.
            var action = new FlightAction(CloseSessionAction, ByteString.Empty);
            using AsyncServerStreamingCall<FlightResult> call = Client.DoAction(
                action,
                new Metadata { { AuthorizationHeader, authorization } },
                CreateDeadline(s_closeSessionTimeout),
                timeout.Token
            );
            await foreach (
                FlightResult _ in call
                    .ResponseStream.ReadAllAsync(timeout.Token)
                    .ConfigureAwait(false)
            )
            {
                // The result body is empty; draining the stream completes the action.
            }
        }
        catch (RpcException)
        {
            // Releasing the server session is best effort; the server expires it otherwise.
        }
        catch (OperationCanceledException)
        {
            // Disposal must not block on an unresponsive server.
        }
    }

    private static DateTime? CreateDeadline(TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan ? null : DateTime.UtcNow.Add(timeout);

    private static string? FindAuthorization(Metadata metadata)
    {
        foreach (Metadata.Entry entry in metadata)
        {
            if (
                entry.Key.Equals(AuthorizationHeader, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(entry.Value)
            )
            {
                return entry.Value;
            }
        }

        return null;
    }
}
