using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using DotRocks.Data.Pooling;
using DotRocks.Data.Protocol.Handshake;

namespace DotRocks.Data.Loading;

/// <summary>
/// Executes StarRocks Stream Load requests over the FE HTTP endpoint.
/// </summary>
public sealed class DotRocksStreamLoadClient : IDisposable
{
    private const int MaximumRedirects = 5;
    private readonly DotRocksConnectionOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly Func<
        DotRocksConnectionOptions,
        CancellationToken,
        ValueTask<DotRocksServerCapabilities?>
    > _capabilityProbe;
    private DotRocksServerCapabilities? _capabilities;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksStreamLoadClient"/> class.
    /// </summary>
    /// <param name="connectionString">The DotRocks connection string.</param>
    public DotRocksStreamLoadClient(string connectionString)
        : this(DotRocksConnectionOptions.Parse(connectionString), CreateDefaultHttpClient(), true)
    { }

    internal DotRocksStreamLoadClient(
        DotRocksConnectionOptions options,
        HttpClient httpClient,
        bool disposeHttpClient,
        Func<
            DotRocksConnectionOptions,
            CancellationToken,
            ValueTask<DotRocksServerCapabilities?>
        >? capabilityProbe = null
    )
    {
        _options = options;
        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _capabilityProbe = capabilityProbe ?? ProbeServerCapabilitiesAsync;
        ValidateTransportSecurity(options);
    }

    /// <summary>
    /// Gets the configured StarRocks Stream Load HTTP endpoint.
    /// </summary>
    public Uri StreamLoadEndpoint => _options.StreamLoadEndpoint;

    /// <summary>
    /// Loads a CSV payload stream into a StarRocks table.
    /// </summary>
    /// <param name="databaseName">The destination database name.</param>
    /// <param name="tableName">The destination table name.</param>
    /// <param name="payload">The CSV payload stream.</param>
    /// <param name="options">The optional Stream Load request options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The successful Stream Load result.</returns>
    public Task<DotRocksStreamLoadResult> LoadCsvAsync(
        string databaseName,
        string tableName,
        Stream payload,
        DotRocksStreamLoadOptions? options = null,
        CancellationToken cancellationToken = default
    ) =>
        LoadAsync(
            DotRocksStreamLoadFormat.Csv,
            databaseName,
            tableName,
            payload,
            options,
            "text/csv",
            cancellationToken
        );

    /// <summary>
    /// Loads a JSON payload stream into a StarRocks table.
    /// </summary>
    /// <param name="databaseName">The destination database name.</param>
    /// <param name="tableName">The destination table name.</param>
    /// <param name="payload">The JSON payload stream.</param>
    /// <param name="options">The optional Stream Load request options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The successful Stream Load result.</returns>
    public Task<DotRocksStreamLoadResult> LoadJsonAsync(
        string databaseName,
        string tableName,
        Stream payload,
        DotRocksStreamLoadOptions? options = null,
        CancellationToken cancellationToken = default
    ) =>
        LoadAsync(
            DotRocksStreamLoadFormat.Json,
            databaseName,
            tableName,
            payload,
            options,
            "application/json",
            cancellationToken
        );

    /// <summary>
    /// Begins a StarRocks Stream Load transaction.
    /// </summary>
    /// <param name="databaseName">The database name for the transaction.</param>
    /// <param name="tableName">The default table name for the transaction.</param>
    /// <param name="options">The transaction options. A label is required.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The active Stream Load transaction.</returns>
    public async Task<DotRocksStreamLoadTransaction> BeginTransactionAsync(
        string databaseName,
        string tableName,
        DotRocksStreamLoadTransactionOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ValidateIdentifier(databaseName, nameof(databaseName));
        ValidateIdentifier(tableName, nameof(tableName));
        ArgumentNullException.ThrowIfNull(options);

        if (options.IsMultiTable)
        {
            await EnsureMultiTableTransactionSupportedAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        DotRocksStreamLoadResult result = await SendTransactionOperationAsync(
                "begin",
                options.BuildBeginHeaders(databaseName, tableName),
                cancellationToken
            )
            .ConfigureAwait(false);
        return new DotRocksStreamLoadTransaction(this, databaseName, tableName, options, result);
    }

    // Multi-table Stream Load transactions are a StarRocks 4.0+ capability; earlier lines are
    // single-table only. Probe the server version once (lazily, only for the multi-table path) and
    // reject before any HTTP call when the version is known to predate support. When the version
    // cannot be determined, defer to the server's own rejection rather than blocking a valid setup.
    private async ValueTask EnsureMultiTableTransactionSupportedAsync(
        CancellationToken cancellationToken
    )
    {
        _capabilities ??= await _capabilityProbe(_options, cancellationToken).ConfigureAwait(false);

        if (_capabilities is { SupportsMultiTableStreamLoadTransaction: false })
        {
            throw new DotRocksStreamLoadException(
                $"Multi-table Stream Load transactions require StarRocks 4.0 or later; the server reports '{_capabilities.ServerVersion.Raw}'. Use single-table transactions on this server."
            );
        }
    }

    private static async ValueTask<DotRocksServerCapabilities?> ProbeServerCapabilitiesAsync(
        DotRocksConnectionOptions options,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using DotRocksPhysicalConnection probe = await DotRocksPhysicalConnection
                .OpenAsync(options, cancellationToken)
                .ConfigureAwait(false);
            return probe.Capabilities;
        }
        catch (DotRocksException)
        {
            // The query port may be unreachable from a Stream-Load-only deployment; fall back to
            // server-side enforcement instead of failing a load that might be valid.
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<DotRocksStreamLoadResult> LoadAsync(
        DotRocksStreamLoadFormat format,
        string databaseName,
        string tableName,
        Stream payload,
        DotRocksStreamLoadOptions? options,
        string mediaType,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ValidateIdentifier(databaseName, nameof(databaseName));
        ValidateIdentifier(tableName, nameof(tableName));
        ArgumentNullException.ThrowIfNull(payload);

        options ??= new DotRocksStreamLoadOptions();
        IReadOnlyDictionary<string, string> headers = options.BuildHeaders(format);
        return await SendPayloadRequestAsync(
                BuildStreamLoadUri(databaseName, tableName),
                headers,
                payload,
                mediaType,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal Task<DotRocksStreamLoadResult> SendTransactionLoadAsync(
        string databaseName,
        string tableName,
        Stream payload,
        IReadOnlyDictionary<string, string> headers,
        string mediaType,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ValidateIdentifier(databaseName, nameof(databaseName));
        ValidateIdentifier(tableName, nameof(tableName));
        ArgumentNullException.ThrowIfNull(payload);
        return SendPayloadRequestAsync(
            BuildTransactionUri("load"),
            headers,
            payload,
            mediaType,
            cancellationToken
        );
    }

    internal Task<DotRocksStreamLoadResult> SendTransactionOperationAsync(
        string operation,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken,
        Action? onRequestDispatch = null
    )
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return SendRequestAsync(
            HttpMethod.Post,
            BuildTransactionUri(operation),
            headers,
            payload: null,
            mediaType: null,
            onRequestDispatch,
            cancellationToken
        );
    }

    private Task<DotRocksStreamLoadResult> SendPayloadRequestAsync(
        Uri requestUri,
        IReadOnlyDictionary<string, string> headers,
        Stream payload,
        string mediaType,
        CancellationToken cancellationToken
    ) =>
        SendRequestAsync(
            HttpMethod.Put,
            requestUri,
            headers,
            payload,
            mediaType,
            onRequestDispatch: null,
            cancellationToken
        );

    private async Task<DotRocksStreamLoadResult> SendRequestAsync(
        HttpMethod method,
        Uri requestUri,
        IReadOnlyDictionary<string, string> headers,
        Stream? payload,
        string? mediaType,
        Action? onRequestDispatch,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        long initialPosition = payload?.CanSeek == true ? payload.Position : 0;

        for (int redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            if (payload?.CanSeek == true)
            {
                payload.Position = initialPosition;
            }

            using var request = CreateRequest(method, requestUri, payload, headers, mediaType);
            HttpResponseMessage response;
            onRequestDispatch?.Invoke();
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            using (response)
            {
                if (IsRedirect(response))
                {
                    if (payload is not null && !payload.CanSeek)
                    {
                        throw new DotRocksStreamLoadException(
                            "StarRocks Stream Load redirected a non-seekable payload stream. Use a seekable stream or send the load directly to the final endpoint."
                        );
                    }

                    requestUri = GetRedirectUri(response, requestUri);
                    ValidateRedirectUri(requestUri);
                    continue;
                }

                string responseText = await response
                    .Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new DotRocksStreamLoadException(
                        $"StarRocks Stream Load request failed with HTTP status {(int)response.StatusCode}.",
                        response.StatusCode,
                        null
                    );
                }

                DotRocksStreamLoadResult result = DotRocksStreamLoadResult.Parse(responseText);
                if (!result.IsSuccess)
                {
                    throw new DotRocksStreamLoadException(
                        $"StarRocks Stream Load failed with status '{result.Status}'.",
                        response.StatusCode,
                        result
                    );
                }

                return result;
            }
        }

        throw new DotRocksStreamLoadException("StarRocks Stream Load exceeded the redirect limit.");
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri requestUri,
        Stream? payload,
        IReadOnlyDictionary<string, string> headers,
        string? mediaType
    )
    {
        var request = new HttpRequestMessage(method, requestUri);
        if (ShouldSendCredentials(requestUri))
        {
            request.Headers.Authorization = CreateAuthorizationHeader();
        }

        // Expect: 100-continue only matters when sending a body; on bodyless transaction
        // control requests it just adds a wasted round-trip (and can hang on some servers).
        request.Headers.ExpectContinue = payload is not null;
        foreach (KeyValuePair<string, string> header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (payload is not null && mediaType is not null)
        {
            request.Content = new StreamContent(new NonDisposingStream(payload));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        return request;
    }

    private Uri BuildStreamLoadUri(string databaseName, string tableName)
    {
        var builder = new UriBuilder(_options.StreamLoadEndpoint)
        {
            Path =
                "/api/"
                + Uri.EscapeDataString(databaseName)
                + "/"
                + Uri.EscapeDataString(tableName)
                + "/_stream_load",
        };
        return builder.Uri;
    }

    private Uri BuildTransactionUri(string operation)
    {
        var builder = new UriBuilder(_options.StreamLoadEndpoint)
        {
            Path = "/api/transaction/" + Uri.EscapeDataString(operation),
        };
        return builder.Uri;
    }

    private AuthenticationHeaderValue CreateAuthorizationHeader()
    {
        string credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(_options.UserId + ":" + _options.Password)
        );
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    // Basic credentials are only forwarded over an encrypted transport, or when the caller
    // has explicitly opted into insecure Stream Load for a trusted local environment. This
    // keeps a redirect from leaking credentials in clear text.
    private bool ShouldSendCredentials(Uri requestUri) =>
        string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        || _options.AllowInsecureStreamLoad;

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient owns and disposes the SocketsHttpHandler through disposeHandler: true."
    )]
    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        return new HttpClient(handler, disposeHandler: true);
    }

    private static bool IsRedirect(HttpResponseMessage response) =>
        response.StatusCode
            is System.Net.HttpStatusCode.MovedPermanently
                or System.Net.HttpStatusCode.Redirect
                or System.Net.HttpStatusCode.TemporaryRedirect
                or System.Net.HttpStatusCode.PermanentRedirect;

    private static Uri GetRedirectUri(HttpResponseMessage response, Uri requestUri)
    {
        Uri? location = response.Headers.Location;
        if (location is null)
        {
            throw new DotRocksStreamLoadException(
                "StarRocks Stream Load redirect response did not include a Location header.",
                response.StatusCode,
                null
            );
        }

        return location.IsAbsoluteUri ? location : new Uri(requestUri, location);
    }

    private void ValidateRedirectUri(Uri redirectUri)
    {
        if (redirectUri.Scheme is not ("http" or "https"))
        {
            throw new DotRocksStreamLoadException(
                "StarRocks Stream Load redirected to an unsupported URI scheme."
            );
        }

        if (!string.IsNullOrEmpty(redirectUri.UserInfo))
        {
            throw new DotRocksStreamLoadException(
                "StarRocks Stream Load redirect URI must not include user information."
            );
        }

        bool redirectIsHttp = string.Equals(
            redirectUri.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase
        );
        bool endpointIsHttps = string.Equals(
            _options.StreamLoadEndpoint.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase
        );

        // Never downgrade a TLS-configured endpoint to plaintext on redirect, even when
        // insecure Stream Load is allowed: it would forward Basic credentials in clear text.
        if (redirectIsHttp && endpointIsHttps)
        {
            throw new DotRocksStreamLoadException(
                "StarRocks Stream Load redirected an HTTPS endpoint to an insecure HTTP endpoint; DotRocks refuses to forward credentials over the downgraded transport."
            );
        }

        if (redirectIsHttp && !_options.AllowInsecureStreamLoad)
        {
            throw new DotRocksStreamLoadException(
                "StarRocks Stream Load redirected to an HTTP endpoint. Use HTTPS or set 'Allow Insecure Stream Load=true' for trusted local test environments."
            );
        }
    }

    private static void ValidateTransportSecurity(DotRocksConnectionOptions options)
    {
        if (
            string.Equals(
                options.StreamLoadEndpoint.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase
            ) && !options.AllowInsecureStreamLoad
        )
        {
            throw new InvalidOperationException(
                "HTTP Stream Load endpoints send Basic authentication without transport encryption. Use HTTPS or set 'Allow Insecure Stream Load=true' for trusted local test environments."
            );
        }
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        if (value.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("Value must not contain path separators.", parameterName);
        }
    }

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => inner.WriteAsync(buffer, cancellationToken);
    }
}
