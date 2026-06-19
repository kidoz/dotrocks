using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotRocks.Data;
using DotRocks.Data.Loading;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class DotRocksStreamLoadClientTests
{
    [Fact]
    public async Task LoadCsvAsync_SendsStreamingPutWithBasicAuthAndHeaders()
    {
        using var handler = new RecordingHandler(
            static async (request, cancellationToken) =>
            {
                string body = await request
                    .Content!.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(true);

                Assert.Equal("1,one\\n", body);
                return JsonResponse(
                    """
                    {
                      "Status": "Success",
                      "Message": "OK",
                      "Label": "dotrocks_label",
                      "NumberTotalRows": 1,
                      "NumberLoadedRows": 1,
                      "NumberFilteredRows": 0,
                      "NumberUnselectedRows": 0,
                      "LoadBytes": 6,
                      "LoadTimeMs": 12
                    }
                    """
                );
            }
        );
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\\n"));

        DotRocksStreamLoadResult result = await client
            .LoadCsvAsync(
                "warehouse",
                "events",
                payload,
                new DotRocksStreamLoadOptions
                {
                    Label = "dotrocks_label",
                    Columns = "id,name",
                    ColumnSeparator = ",",
                    RowDelimiter = "\\n",
                    StrictMode = true,
                    MaxFilterRatio = 0.25,
                },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        Assert.NotNull(handler.Request);
        HttpRequestMessage request = handler.Request;
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(
            "http://starrocks.local:8030/api/warehouse/events/_stream_load",
            request.RequestUri!.AbsoluteUri
        );
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(
            ExpectedBasicToken("alice", "secret"),
            request.Headers.Authorization.Parameter
        );
        Assert.True(request.Headers.ExpectContinue);
        AssertHeader(request.Headers, "format", "csv");
        AssertHeader(request.Headers, "label", "dotrocks_label");
        AssertHeader(request.Headers, "columns", "id,name");
        AssertHeader(request.Headers, "column_separator", ",");
        AssertHeader(request.Headers, "row_delimiter", "\\n");
        AssertHeader(request.Headers, "strict_mode", "true");
        AssertHeader(request.Headers, "max_filter_ratio", "0.25");
        Assert.Equal("text/csv", request.Content!.Headers.ContentType!.MediaType);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.NumberLoadedRows);
    }

    [Fact]
    public async Task LoadJsonAsync_SendsJsonHeaders()
    {
        using var handler = new RecordingHandler(static (_, _) => Task.FromResult(JsonResponse()));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("[{\"id\":1}]"));

        await client
            .LoadJsonAsync(
                "warehouse",
                "events",
                payload,
                new DotRocksStreamLoadOptions { StripOuterArray = true, JsonPaths = "$.id" },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        Assert.NotNull(handler.Request);
        HttpRequestMessage request = handler.Request;
        AssertHeader(request.Headers, "format", "json");
        AssertHeader(request.Headers, "strip_outer_array", "true");
        AssertHeader(request.Headers, "jsonpaths", "$.id");
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task LoadCsvAsync_FollowsTemporaryRedirectWithoutBuffering()
    {
        using var handler = new RedirectingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\\n"));

        DotRocksStreamLoadResult result = await client
            .LoadCsvAsync(
                "warehouse",
                "events",
                payload,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "http://starrocks.local:8030/api/warehouse/events/_stream_load",
            handler.Requests[0].RequestUri!.AbsoluteUri
        );
        Assert.Equal(
            "http://be.starrocks.local:8040/api/warehouse/events/_stream_load",
            handler.Requests[1].RequestUri!.AbsoluteUri
        );
        Assert.Equal("1,one\\n", handler.RedirectedBody);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Put, request.Method));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad/name")]
    public async Task LoadCsvAsync_InvalidDatabase_Throws(string databaseName)
    {
        using var handler = new RecordingHandler(static (_, _) => Task.FromResult(JsonResponse()));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new MemoryStream([1]);

        await Assert
            .ThrowsAsync<ArgumentException>(async () =>
                await client
                    .LoadCsvAsync(
                        databaseName,
                        "events",
                        payload,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task LoadCsvAsync_InvalidOptions_ThrowsBeforeSending()
    {
        using var handler = new RecordingHandler(static (_, _) => Task.FromResult(JsonResponse()));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new MemoryStream([1]);

        await Assert
            .ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await client
                    .LoadCsvAsync(
                        "warehouse",
                        "events",
                        payload,
                        new DotRocksStreamLoadOptions { MaxFilterRatio = 1.5 },
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task LoadCsvAsync_HeaderInjection_ThrowsBeforeSending()
    {
        using var handler = new RecordingHandler(static (_, _) => Task.FromResult(JsonResponse()));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new MemoryStream([1]);

        await Assert
            .ThrowsAsync<ArgumentException>(async () =>
                await client
                    .LoadCsvAsync(
                        "warehouse",
                        "events",
                        payload,
                        new DotRocksStreamLoadOptions { Label = "bad\r\nheader" },
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task LoadCsvAsync_StarRocksFailure_ThrowsWithParsedResult()
    {
        using var handler = new RecordingHandler(
            static (_, _) =>
                Task.FromResult(
                    JsonResponse(
                        """
                        {
                          "Status": "Fail",
                          "Message": "filtered rows",
                          "NumberTotalRows": "2",
                          "NumberLoadedRows": "1",
                          "NumberFilteredRows": "1",
                          "ErrorURL": "http://starrocks.local/error"
                        }
                        """
                    )
                )
        );
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new MemoryStream([1]);

        DotRocksStreamLoadException exception = await Assert
            .ThrowsAsync<DotRocksStreamLoadException>(async () =>
                await client
                    .LoadCsvAsync(
                        "warehouse",
                        "events",
                        payload,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, exception.HttpStatusCode);
        Assert.NotNull(exception.Result);
        DotRocksStreamLoadResult result = exception.Result;
        Assert.Equal("Fail", result.Status);
        Assert.Equal(1, result.NumberFilteredRows);
    }

    [Fact]
    public async Task LoadCsvAsync_HttpFailure_ThrowsWithoutResponseBodyLeak()
    {
        using var handler = new RecordingHandler(
            static (_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("secret diagnostic"),
                    }
                )
        );
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new MemoryStream([1]);

        DotRocksStreamLoadException exception = await Assert
            .ThrowsAsync<DotRocksStreamLoadException>(async () =>
                await client
                    .LoadCsvAsync(
                        "warehouse",
                        "events",
                        payload,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, exception.HttpStatusCode);
        Assert.Null(exception.Result);
        Assert.DoesNotContain("secret diagnostic", exception.ToString(), StringComparison.Ordinal);
    }

    private static DotRocksStreamLoadClient CreateClient(HttpClient httpClient)
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=starrocks.local;User ID=alice;Password=secret"
        );
        return new DotRocksStreamLoadClient(options, httpClient, disposeHttpClient: false);
    }

    private static HttpResponseMessage JsonResponse(
        string json =
            """
                {
                  "Status": "Success",
                  "Message": "OK",
                  "NumberTotalRows": 0,
                  "NumberLoadedRows": 0,
                  "NumberFilteredRows": 0,
                  "NumberUnselectedRows": 0,
                  "LoadBytes": 0,
                  "LoadTimeMs": 0
                }
                """
    ) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string ExpectedBasicToken(string userName, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(userName + ":" + password));

    private static void AssertHeader(HttpHeaders headers, string name, string expectedValue)
    {
        Assert.True(headers.TryGetValues(name, out IEnumerable<string>? values));
        Assert.Contains(expectedValue, values, StringComparer.Ordinal);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder
    ) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Request = request;
            return responder(request, cancellationToken);
        }
    }

    private sealed class RedirectingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public string? RedirectedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers =
                    {
                        Location = new Uri(
                            "http://be.starrocks.local:8040/api/warehouse/events/_stream_load"
                        ),
                    },
                };
            }

            RedirectedBody = await request
                .Content!.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(true);
            return JsonResponse();
        }
    }
}
