using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotRocks.Data.Diagnostics;

/// <summary>
/// Exposes the DotRocks diagnostic source and metrics names so applications can subscribe with
/// <c>ActivityListener</c>/OpenTelemetry tracing and <c>MeterListener</c>/OpenTelemetry metrics.
/// </summary>
public static class DotRocksTelemetry
{
    /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> name for DotRocks tracing.</summary>
    public const string ActivitySourceName = "DotRocks.Data";

    /// <summary>The <see cref="System.Diagnostics.Metrics.Meter"/> name for DotRocks metrics.</summary>
    public const string MeterName = "DotRocks.Data";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    internal static readonly Meter Meter = new(MeterName, "1.0.0");

    internal static readonly Counter<long> ConnectionsOpened = Meter.CreateCounter<long>(
        "dotrocks.connections.opened",
        unit: "{connection}",
        description: "Number of physical or pooled StarRocks connections opened."
    );

    internal static readonly Histogram<double> ConnectionOpenDuration =
        Meter.CreateHistogram<double>(
            "dotrocks.connection.open.duration",
            unit: "ms",
            description: "Duration of opening a StarRocks connection (pool acquisition and physical open) in milliseconds."
        );

    internal static readonly Histogram<double> TransactionDuration = Meter.CreateHistogram<double>(
        "dotrocks.transaction.duration",
        unit: "ms",
        description: "Duration of a StarRocks transaction from begin to commit or rollback in milliseconds."
    );

    internal static readonly Counter<long> CommandsExecuted = Meter.CreateCounter<long>(
        "dotrocks.commands.executed",
        unit: "{command}",
        description: "Number of StarRocks commands executed."
    );

    internal static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "dotrocks.command.duration",
        unit: "ms",
        description: "Duration of StarRocks command execution in milliseconds."
    );

    internal static readonly Histogram<double> PoolLeaseWaitDuration =
        Meter.CreateHistogram<double>(
            "dotrocks.pool.lease.wait_time",
            unit: "ms",
            description: "Time spent waiting to acquire a pooled StarRocks connection lease in milliseconds."
        );

    internal static readonly Counter<long> PoolConnectionsDiscarded = Meter.CreateCounter<long>(
        "dotrocks.pool.connections.discarded",
        unit: "{connection}",
        description: "Number of pooled StarRocks connections discarded on return instead of reused."
    );

    internal static readonly Histogram<double> StreamLoadDuration = Meter.CreateHistogram<double>(
        "dotrocks.stream_load.duration",
        unit: "ms",
        description: "Duration of a StarRocks Stream Load request in milliseconds."
    );

    internal static readonly Counter<long> StreamLoadRowsLoaded = Meter.CreateCounter<long>(
        "dotrocks.stream_load.rows_loaded",
        unit: "{row}",
        description: "Number of rows loaded by StarRocks Stream Load."
    );

    internal static readonly Counter<long> StreamLoadRowsFiltered = Meter.CreateCounter<long>(
        "dotrocks.stream_load.rows_filtered",
        unit: "{row}",
        description: "Number of rows filtered (rejected) by StarRocks Stream Load."
    );

    internal static readonly Counter<long> StreamLoadBytes = Meter.CreateCounter<long>(
        "dotrocks.stream_load.bytes",
        unit: "By",
        description: "Number of payload bytes accepted by StarRocks Stream Load."
    );
}
