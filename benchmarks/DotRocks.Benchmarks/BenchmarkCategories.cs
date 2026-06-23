namespace DotRocks.Benchmarks;

/// <summary>
/// BenchmarkDotNet category names used to separate benchmark kinds.
/// </summary>
internal static class BenchmarkCategories
{
    /// <summary>
    /// Deterministic, server-free microbenchmarks that the performance budget gate runs and
    /// validates. Run with <c>--anyCategories Local</c>.
    /// </summary>
    public const string Local = "Local";

    /// <summary>
    /// Benchmarks that require a live StarRocks server. These are observational (lease latency,
    /// streaming throughput) rather than deterministic microbenchmarks, so they are excluded from
    /// the performance budget gate and run via a dedicated recipe against a server. Run with
    /// <c>--anyCategories ServerBacked</c>.
    /// </summary>
    public const string ServerBacked = "ServerBacked";
}
