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
    public async Task LoadCsvAsync_PublishTimeoutIsTreatedAsSuccess()
    {
        using var handler = new RecordingHandler(
            static (_, _) =>
                Task.FromResult(
                    JsonResponse(
                        """
                        { "Status": "Publish Timeout", "Message": "publish timeout", "NumberLoadedRows": 1 }
                        """
                    )
                )
        );
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
        Assert.True(result.IsPublishTimeout);
    }

    [Fact]
    public async Task LoadCsvAsync_WithoutLabel_GeneratesIdempotencyLabel()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse()));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\\n"));

        _ = await client
            .LoadCsvAsync(
                "warehouse",
                "events",
                payload,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        Assert.NotNull(handler.Request);
        Assert.True(handler.Request.Headers.TryGetValues("label", out IEnumerable<string>? labels));
        Assert.StartsWith("dotrocks_", labels!.Single(), StringComparison.Ordinal);
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

    [Fact]
    public void Constructor_HttpEndpointWithoutExplicitOptIn_Throws()
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=starrocks.local;User ID=alice;Password=secret"
        );
        using var handler = new RecordingHandler(static (_, _) => Task.FromResult(JsonResponse()));
        using var httpClient = new HttpClient(handler);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new DotRocksStreamLoadClient(options, httpClient, disposeHttpClient: false)
        );

        Assert.Contains("HTTP Stream Load", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Allow Insecure Stream Load", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCsvAsync_RedirectWithNonSeekablePayload_Throws()
    {
        using var handler = new RedirectingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new NonSeekableStream(Encoding.UTF8.GetBytes("1,one\\n"));

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

        Assert.Contains("non-seekable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LoadCsvAsync_HttpsEndpointRedirectToHttpWithoutOptIn_ThrowsBeforeForwardingAuth()
    {
        using var handler = new SingleRedirectHandler(
            new Uri("http://be.starrocks.local:8040/api/warehouse/events/_stream_load")
        );
        using var httpClient = new HttpClient(handler);
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=starrocks.local;User ID=alice;Password=secret;Stream Load Endpoint=https://starrocks.local:8030"
        );
        using var client = new DotRocksStreamLoadClient(
            options,
            httpClient,
            disposeHttpClient: false
        );
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\\n"));

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

        Assert.Contains("HTTP endpoint", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LoadCsvAsync_HttpsEndpointRedirectToHttpEvenWithOptIn_RefusesDowngrade()
    {
        using var handler = new SingleRedirectHandler(
            new Uri("http://be.starrocks.local:8040/api/warehouse/events/_stream_load")
        );
        using var httpClient = new HttpClient(handler);
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=starrocks.local;User ID=alice;Password=secret;Stream Load Endpoint=https://starrocks.local:8030;Allow Insecure Stream Load=true"
        );
        using var client = new DotRocksStreamLoadClient(
            options,
            httpClient,
            disposeHttpClient: false
        );
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\\n"));

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

        Assert.Contains("downgraded", exception.Message, StringComparison.OrdinalIgnoreCase);
        // Only the initial HTTPS request is made; the insecure redirect target is never contacted.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LoadCsvAsync_RedirectWithUserInfo_ThrowsBeforeForwardingAuth()
    {
        using var handler = new SingleRedirectHandler(
            new Uri("https://alice@be.starrocks.local:8040/api/warehouse/events/_stream_load")
        );
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\\n"));

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

        Assert.Contains("user information", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
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
    public async Task LoadCsvAsync_StarRocksFailure_DoesNotCopySensitiveResultMessageIntoException()
    {
        const string secret = "stream-load-secret-value";
        using var handler = new RecordingHandler(
            static (_, _) =>
                Task.FromResult(
                    JsonResponse(
                        """
                        {
                          "Status": "Fail",
                          "Message": "filtered row contained stream-load-secret-value",
                          "NumberTotalRows": "1",
                          "NumberLoadedRows": "0",
                          "NumberFilteredRows": "1"
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

        Assert.NotNull(exception.Result);
        Assert.Contains(secret, exception.Result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            ExpectedBasicToken("alice", "secret"),
            exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("alice:secret", exception.ToString(), StringComparison.Ordinal);
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

    [Fact]
    public async Task BeginTransactionAsync_SendsTransactionHeaders()
    {
        using var handler = new RecordingHandler(
            static (_, _) =>
                Task.FromResult(
                    JsonResponse(
                        """
                        {
                          "Status": "OK",
                          "Message": "",
                          "Label": "tx_label",
                          "TxnId": 42
                        }
                        """
                    )
                )
        );
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        DotRocksStreamLoadTransaction transaction = await client
            .BeginTransactionAsync(
                "warehouse",
                "events",
                new DotRocksStreamLoadTransactionOptions
                {
                    Label = "tx_label",
                    Timeout = TimeSpan.FromSeconds(30),
                    IdleTimeout = TimeSpan.FromSeconds(10),
                },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        Assert.Equal("tx_label", transaction.Label);
        Assert.Equal(42, transaction.BeginResult.TransactionId);
        Assert.NotNull(handler.Request);
        HttpRequestMessage request = handler.Request;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "http://starrocks.local:8030/api/transaction/begin",
            request.RequestUri!.AbsoluteUri
        );
        // A bodyless transaction control request must not send Expect: 100-continue.
        Assert.NotEqual(true, request.Headers.ExpectContinue);
        AssertHeader(request.Headers, "label", "tx_label");
        AssertHeader(request.Headers, "db", "warehouse");
        AssertHeader(request.Headers, "table", "events");
        AssertHeader(request.Headers, "timeout", "30");
        AssertHeader(request.Headers, "idle_transaction_timeout", "10");
    }

    [Fact]
    public async Task TransactionLoadCsvAsync_FollowsRedirectAndPreservesPayload()
    {
        using var handler = new TransactionRedirectingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        DotRocksStreamLoadTransaction transaction = await client
            .BeginTransactionAsync(
                "warehouse",
                "events",
                new DotRocksStreamLoadTransactionOptions { Label = "tx_label" },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("1,one\\n"));

        DotRocksStreamLoadResult result = await transaction
            .LoadCsvAsync(
                payload,
                new DotRocksStreamLoadOptions { Columns = "id,name", ColumnSeparator = "," },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(
            "http://starrocks.local:8030/api/transaction/load",
            handler.Requests[1].RequestUri!.AbsoluteUri
        );
        Assert.Equal(
            "http://be.starrocks.local:8040/api/transaction/load",
            handler.Requests[2].RequestUri!.AbsoluteUri
        );
        Assert.Equal("1,one\\n", handler.RedirectedBody);
        AssertHeader(handler.Requests[2].Headers, "label", "tx_label");
        AssertHeader(handler.Requests[2].Headers, "db", "warehouse");
        AssertHeader(handler.Requests[2].Headers, "table", "events");
        AssertHeader(handler.Requests[2].Headers, "columns", "id,name");
    }

    [Fact]
    public async Task TransactionPrepareCommit_RejectsDoubleCompletion()
    {
        using var handler = new TransactionSequenceHandler();
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        DotRocksStreamLoadTransaction transaction = await client
            .BeginTransactionAsync(
                "warehouse",
                "events",
                new DotRocksStreamLoadTransactionOptions
                {
                    Label = "tx_label",
                    PreparedTimeout = TimeSpan.FromSeconds(60),
                },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        await transaction.PrepareAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        DotRocksStreamLoadResult commit = await transaction
            .CommitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(commit.IsSuccess);
        await Assert
            .ThrowsAsync<InvalidOperationException>(async () =>
                await transaction
                    .CommitAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
        Assert.Equal(3, handler.Requests.Count);
        AssertHeader(handler.Requests[1].Headers, "prepared_timeout", "60");
    }

    [Fact]
    public async Task TransactionRollback_RejectsDoubleCompletion()
    {
        using var handler = new TransactionSequenceHandler();
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        DotRocksStreamLoadTransaction transaction = await client
            .BeginTransactionAsync(
                "warehouse",
                "events",
                new DotRocksStreamLoadTransactionOptions { Label = "tx_label" },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        DotRocksStreamLoadResult rollback = await transaction
            .RollbackAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(rollback.IsSuccess);
        await Assert
            .ThrowsAsync<InvalidOperationException>(async () =>
                await transaction
                    .RollbackAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task TransactionCommitServerFailure_ReportsInDoubt()
    {
        using var handler = new TransactionSequenceHandler(commitSucceeds: false);
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        DotRocksStreamLoadTransaction transaction = await client
            .BeginTransactionAsync(
                "warehouse",
                "events",
                new DotRocksStreamLoadTransactionOptions { Label = "tx_label" },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        await transaction.PrepareAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        // A commit that reaches the server but returns a failure status is indeterminate, not a
        // clean failure: the commit may have applied server-side.
        await Assert
            .ThrowsAsync<DotRocksStreamLoadTransactionInDoubtException>(async () =>
                await transaction
                    .CommitAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        // An in-doubt transaction cannot be rolled back or re-committed.
        await Assert
            .ThrowsAsync<InvalidOperationException>(async () =>
                await transaction
                    .RollbackAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task TransactionRollbackServerFailure_MarksTransactionFailed()
    {
        using var handler = new TransactionSequenceHandler(
            commitSucceeds: false,
            failRollback: true
        );
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        DotRocksStreamLoadTransaction transaction = await client
            .BeginTransactionAsync(
                "warehouse",
                "events",
                new DotRocksStreamLoadTransactionOptions { Label = "tx_label" },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        // A failed rollback is a hard failure (the data was never committed), not in-doubt.
        await Assert
            .ThrowsAsync<DotRocksStreamLoadException>(async () =>
                await transaction
                    .RollbackAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task BeginTransactionAsync_WithPreCanceledToken_ThrowsOperationCanceled()
    {
        using var handler = new RecordingHandler(static (_, _) => Task.FromResult(JsonResponse()));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert
            .ThrowsAsync<OperationCanceledException>(async () =>
                await client
                    .BeginTransactionAsync(
                        "warehouse",
                        "events",
                        new DotRocksStreamLoadTransactionOptions { Label = "tx_label" },
                        cancellation.Token
                    )
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task TransactionCommitAsync_CancellationAfterRequestSent_ReportsInDoubt()
    {
        using var handler = new TransactionCompletionFailureHandler(
            "/api/transaction/commit",
            static () => new OperationCanceledException("commit timed out")
        );
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        DotRocksStreamLoadTransaction transaction = await CreatePreparedTransactionAsync(client)
            .ConfigureAwait(true);

        DotRocksStreamLoadTransactionInDoubtException exception = await Assert
            .ThrowsAsync<DotRocksStreamLoadTransactionInDoubtException>(async () =>
                await transaction
                    .CommitAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal("tx_label", exception.Label);
        Assert.Equal("commit", exception.Operation);
        Assert.IsType<OperationCanceledException>(exception.InnerException);
        await Assert
            .ThrowsAsync<InvalidOperationException>(async () =>
                await transaction
                    .RollbackAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task TransactionRollbackAsync_IoFailureAfterRequestSent_ReportsInDoubt()
    {
        using var handler = new TransactionCompletionFailureHandler(
            "/api/transaction/rollback",
            static () => new HttpRequestException("connection reset")
        );
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        DotRocksStreamLoadTransaction transaction = await client
            .BeginTransactionAsync(
                "warehouse",
                "events",
                new DotRocksStreamLoadTransactionOptions { Label = "tx_label" },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        DotRocksStreamLoadTransactionInDoubtException exception = await Assert
            .ThrowsAsync<DotRocksStreamLoadTransactionInDoubtException>(async () =>
                await transaction
                    .RollbackAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal("tx_label", exception.Label);
        Assert.Equal("rollback", exception.Operation);
        Assert.IsType<HttpRequestException>(exception.InnerException);
        await Assert
            .ThrowsAsync<InvalidOperationException>(async () =>
                await transaction
                    .RollbackAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
    }

    private static async Task<DotRocksStreamLoadTransaction> CreatePreparedTransactionAsync(
        DotRocksStreamLoadClient client
    )
    {
        DotRocksStreamLoadTransaction transaction = await client
            .BeginTransactionAsync(
                "warehouse",
                "events",
                new DotRocksStreamLoadTransactionOptions { Label = "tx_label" },
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await transaction.PrepareAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        return transaction;
    }

    private static DotRocksStreamLoadClient CreateClient(HttpClient httpClient)
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=starrocks.local;User ID=alice;Password=secret;Allow Insecure Stream Load=true"
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

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override long Position
        {
            get => base.Position;
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
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

    private sealed class SingleRedirectHandler(Uri location) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = location },
                }
            );
        }
    }

    private sealed class TransactionRedirectingHandler : HttpMessageHandler
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
                return TransactionResponse();
            }

            if (Requests.Count == 2)
            {
                return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers =
                    {
                        Location = new Uri("http://be.starrocks.local:8040/api/transaction/load"),
                    },
                };
            }

            RedirectedBody = await request
                .Content!.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(true);
            return TransactionResponse();
        }
    }

    private sealed class TransactionSequenceHandler(
        bool commitSucceeds = true,
        bool failRollback = false
    ) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            string path = request.RequestUri!.AbsolutePath;
            bool commitFailed =
                path.EndsWith("/commit", StringComparison.Ordinal) && !commitSucceeds;
            bool rollbackFailed =
                path.EndsWith("/rollback", StringComparison.Ordinal) && failRollback;
            if (commitFailed || rollbackFailed)
            {
                return Task.FromResult(
                    TransactionResponse(
                        """
                        {
                          "Status": "FAILED",
                          "Message": "commit timeout",
                          "Label": "tx_label",
                          "TxnId": 42
                        }
                        """
                    )
                );
            }

            return Task.FromResult(TransactionResponse());
        }
    }

    private sealed class TransactionCompletionFailureHandler(
        string failingPath,
        Func<Exception> exceptionFactory
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string path = request.RequestUri!.AbsolutePath;
            if (string.Equals(path, failingPath, StringComparison.Ordinal))
            {
                throw exceptionFactory();
            }

            return Task.FromResult(TransactionResponse());
        }
    }

    private static HttpResponseMessage TransactionResponse(
        string json =
            """
                {
                  "Status": "OK",
                  "Message": "",
                  "Label": "tx_label",
                  "TxnId": 42,
                  "NumberTotalRows": 1,
                  "NumberLoadedRows": 1,
                  "NumberFilteredRows": 0,
                  "NumberUnselectedRows": 0,
                  "LoadBytes": 6,
                  "LoadTimeMs": 1
                }
                """
    ) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
}
