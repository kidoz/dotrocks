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
}
