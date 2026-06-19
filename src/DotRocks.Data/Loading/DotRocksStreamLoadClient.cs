using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;

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
        bool disposeHttpClient
    )
    {
        _options = options;
        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
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
        Uri requestUri = BuildStreamLoadUri(databaseName, tableName);
        long initialPosition = payload.CanSeek ? payload.Position : 0;

        for (int redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            if (payload.CanSeek)
            {
                payload.Position = initialPosition;
            }

            using var request = CreateRequest(requestUri, payload, headers, mediaType);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (IsRedirect(response))
            {
                requestUri = GetRedirectUri(response, requestUri);
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
                string suffix = string.IsNullOrWhiteSpace(result.Message)
                    ? string.Empty
                    : ": " + result.Message;
                throw new DotRocksStreamLoadException(
                    $"StarRocks Stream Load failed with status '{result.Status}'{suffix}.",
                    response.StatusCode,
                    result
                );
            }

            return result;
        }

        throw new DotRocksStreamLoadException("StarRocks Stream Load exceeded the redirect limit.");
    }

    private HttpRequestMessage CreateRequest(
        Uri requestUri,
        Stream payload,
        IReadOnlyDictionary<string, string> headers,
        string mediaType
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.Authorization = CreateAuthorizationHeader();
        request.Headers.ExpectContinue = true;
        foreach (KeyValuePair<string, string> header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Content = new StreamContent(new NonDisposingStream(payload));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
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

    private AuthenticationHeaderValue CreateAuthorizationHeader()
    {
        string credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(_options.UserId + ":" + _options.Password)
        );
        return new AuthenticationHeaderValue("Basic", credentials);
    }

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
