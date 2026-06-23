using System.Diagnostics;
using DotRocks.Data;
using DotRocks.Data.Diagnostics;
using DotRocks.Data.Loading;
using Xunit;

namespace DotRocks.Protocol.Tests.Diagnostics;

public sealed class TelemetryTagsTests
{
    [Fact]
    public void TagConnectionOpen_EmitsSafeAttributesOnly()
    {
        using Activity activity = StartSampledActivity();
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=warehouse.internal;Port=9030;User ID=alice;Password=topsecret;Database=sales"
        );

        DotRocksTelemetryTags.TagConnectionOpen(activity, options);

        Assert.Equal("other_sql", activity.GetTagItem("db.system.name"));
        Assert.Equal(9030, activity.GetTagItem("server.port"));
        Assert.Equal("sales", activity.GetTagItem("db.namespace"));
        // server.address is intentionally omitted by default.
        Assert.Null(activity.GetTagItem("server.address"));
        AssertNoSecretLeak(activity, "topsecret", "alice", "warehouse.internal");
    }

    [Theory]
    [InlineData("SELECT * FROM t WHERE name = 'secret'", "SELECT")]
    [InlineData("  insert into t values (1)", "INSERT")]
    [InlineData("USE analytics", "USE")]
    [InlineData("SET SESSION x = 1", "SET")]
    [InlineData("VACUUM something", "OTHER")]
    public void TagCommandStart_EmitsLowCardinalityOperationWithoutLiterals(
        string sql,
        string expectedOperation
    )
    {
        using Activity activity = StartSampledActivity();

        DotRocksTelemetryTags.TagCommandStart(activity, sql);

        Assert.Equal("other_sql", activity.GetTagItem("db.system.name"));
        Assert.Equal(expectedOperation, activity.GetTagItem("db.operation.name"));
        Assert.Equal(expectedOperation, activity.GetTagItem("db.query.summary"));
        Assert.Null(activity.GetTagItem("db.query.text"));
        AssertNoSecretLeak(activity, "secret");
    }

    [Fact]
    public void TagError_SetsClassificationWithoutMessageText()
    {
        using Activity activity = StartSampledActivity();

        DotRocksTelemetryTags.TagError(activity, DotRocksTelemetryTags.ErrorTimeout, null);

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Null(activity.StatusDescription);
        Assert.Equal("timeout", activity.GetTagItem("error.type"));
        Assert.Null(activity.GetTagItem("db.response.status_code"));
    }

    [Fact]
    public void Classify_MapsKnownExceptions()
    {
        Assert.Equal(
            ("canceled", (string?)null),
            DotRocksTelemetryTags.Classify(new OperationCanceledException("ignored message"))
        );

        var serverError = new DotRocksException(
            "syntax error near 'secret'",
            serverErrorCode: 1064,
            sqlState: "42000",
            isTransient: false,
            connectionId: null
        );
        (string ErrorType, string? StatusCode) classified = DotRocksTelemetryTags.Classify(
            serverError
        );
        Assert.Equal("42000", classified.ErrorType);
        Assert.Equal("1064", classified.StatusCode);
        Assert.DoesNotContain("secret", classified.ErrorType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionOpenFailure_SpanCarriesNoSecrets()
    {
        // The listener is process-wide and other test classes run in parallel, so capture into a
        // thread-safe collection and assert across all connection-open spans rather than expecting
        // exactly one.
        var captured = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source =>
                string.Equals(source.Name, "DotRocks.Data", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        // Port 1 refuses connections, so OpenAsync fails without a server and quickly.
        const string connectionString =
            "Server=127.0.0.1;Port=1;User ID=alice;Password=topsecret;Connection Timeout=2";
        using var connection = new DotRocksConnection(connectionString);
        await Assert
            .ThrowsAnyAsync<Exception>(() =>
                connection.OpenAsync(TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);

        Activity[] openSpans = captured
            .Where(activity =>
                string.Equals(
                    activity.OperationName,
                    "dotrocks.connection.open",
                    StringComparison.Ordinal
                )
            )
            .ToArray();

        // Our failed open produced an error span; no connection-open span may carry secrets or a
        // raw status message.
        Assert.Contains(openSpans, span => span.Status == ActivityStatusCode.Error);
        foreach (Activity span in openSpans)
        {
            Assert.Null(span.StatusDescription);
            AssertNoSecretLeak(span, "topsecret", "alice", connectionString);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The source and listener must outlive the returned activity; they are reclaimed at process exit in this short-lived test."
    )]
    private static Activity StartSampledActivity()
    {
        var source = new ActivitySource("DotRocks.Tests." + Guid.NewGuid().ToString("N"));
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate =>
                string.Equals(candidate.Name, source.Name, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return source.StartActivity("test")
            ?? throw new InvalidOperationException("Activity was not sampled.");
    }

    private static void AssertNoSecretLeak(Activity activity, params string[] secrets)
    {
        foreach (KeyValuePair<string, object?> tag in activity.TagObjects)
        {
            string? value = tag.Value?.ToString();
            if (value is null)
            {
                continue;
            }

            foreach (string secret in secrets)
            {
                Assert.DoesNotContain(secret, value, StringComparison.Ordinal);
            }
        }

        if (activity.StatusDescription is { } description)
        {
            foreach (string secret in secrets)
            {
                Assert.DoesNotContain(secret, description, StringComparison.Ordinal);
            }
        }
    }
}
