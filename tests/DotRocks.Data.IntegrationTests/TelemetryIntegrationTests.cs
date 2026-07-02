using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

[Collection("StarRocks integration")]
public sealed class TelemetryIntegrationTests
{
    [Fact]
    public async Task ServerError_MapsToSafeStatusCodeAndErrorTypeWithoutMessage()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var capture = new TelemetryCapture();
        await Assert
            .ThrowsAsync<DotRocksException>(() =>
                ExecuteScalarAsync("SELECT FROM", TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);

        Activity span = capture.SingleCommandSpan();
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.NotNull(span.GetTagItem("error.type"));
        Assert.NotNull(span.GetTagItem("db.response.status_code"));
        Assert.Equal("error", capture.SingleCommandOutcome());
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutOutcome()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var capture = new TelemetryCapture();
        await Assert
            .ThrowsAsync<DotRocksException>(() =>
                ExecuteScalarAsync(
                    "SELECT SLEEP(3)",
                    TestContext.Current.CancellationToken,
                    timeout: 1
                )
            )
            .ConfigureAwait(true);

        Assert.Equal("timeout", capture.SingleCommandSpan().GetTagItem("error.type"));
        Assert.Equal("timeout", capture.SingleCommandOutcome());
    }

    [Fact]
    public async Task UserCancellation_MapsToCanceledOutcome()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var capture = new TelemetryCapture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert
            .ThrowsAsync<OperationCanceledException>(() =>
                ExecuteScalarAsync("SELECT SLEEP(3)", cancellation.Token, timeout: 0)
            )
            .ConfigureAwait(true);

        Assert.Equal("canceled", capture.SingleCommandSpan().GetTagItem("error.type"));
        Assert.Equal("canceled", capture.SingleCommandOutcome());
    }

    [Fact]
    public async Task Success_RecordsOperationAndSuccessOutcome()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var capture = new TelemetryCapture();
        _ = await ExecuteScalarAsync("SELECT 1", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Activity span = capture.SingleCommandSpan();
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal("SELECT", span.GetTagItem("db.operation.name"));
        Assert.Equal("success", capture.SingleCommandOutcome());
        Assert.Equal("SELECT", capture.SingleCommandOperation());
    }

    [Fact]
    public async Task ConnectionOpenAndTransactionDurations_AreRecorded()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        var durations = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, "DotRocks.Data", StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>(
            (instrument, _, tags, _) =>
            {
                string? outcome = null;
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    if (string.Equals(tag.Key, "outcome", StringComparison.Ordinal))
                    {
                        outcome = tag.Value?.ToString();
                    }
                }

                durations[instrument.Name] = outcome;
            }
        );
        listener.Start();

        using (var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            DbTransaction transaction = await connection
                .BeginTransactionAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await transaction
                .CommitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }

        listener.Dispose();

        Assert.True(
            durations.TryGetValue("dotrocks.connection.open.duration", out string? openOutcome)
        );
        Assert.Equal("success", openOutcome);
        Assert.True(durations.TryGetValue("dotrocks.transaction.duration", out string? txOutcome));
        Assert.Equal("committed", txOutcome);
    }

    private static async Task<object?> ExecuteScalarAsync(
        string sql,
        CancellationToken cancellationToken,
        int timeout = 30
    )
    {
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(true);

#pragma warning disable CA2100 // Integration test SQL is fixed, not user input.
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
#pragma warning restore CA2100
        command.CommandTimeout = timeout;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(true);
    }

    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;
        private readonly ConcurrentQueue<Activity> _spans = new();
        private readonly ConcurrentQueue<IReadOnlyDictionary<string, object?>> _commandMetrics =
            new();

        public TelemetryCapture()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source =>
                    string.Equals(source.Name, "DotRocks.Data", StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _spans.Enqueue,
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (
                        string.Equals(
                            instrument.Meter.Name,
                            "DotRocks.Data",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _meterListener.SetMeasurementEventCallback<long>(OnLongMeasurement);
            _meterListener.Start();
        }

        public Activity SingleCommandSpan() =>
            Assert.Single(
                _spans,
                span =>
                    string.Equals(
                        span.OperationName,
                        "dotrocks.command.execute",
                        StringComparison.Ordinal
                    )
            );

        public string? SingleCommandOutcome() => SingleCommandMetricTag("outcome");

        public string? SingleCommandOperation() => SingleCommandMetricTag("operation");

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }

        private string? SingleCommandMetricTag(string tag)
        {
            IReadOnlyDictionary<string, object?> measurement = Assert.Single(_commandMetrics);
            return measurement.TryGetValue(tag, out object? value) ? value?.ToString() : null;
        }

        private void OnLongMeasurement(
            Instrument instrument,
            long measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state
        )
        {
            if (
                !string.Equals(
                    instrument.Name,
                    "dotrocks.commands.executed",
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            _commandMetrics.Enqueue(copy);
        }
    }
}
