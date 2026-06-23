using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BenchmarkDotNet.Reports;

namespace DotRocks.Benchmarks;

internal sealed record PerformanceBudget(
    string BenchmarkName,
    double MaxMeanNanoseconds,
    long MaxAllocatedBytes
);

internal sealed record PerformanceBudgetMeasurement(
    string BenchmarkName,
    double MeanNanoseconds,
    long? AllocatedBytes
);

internal sealed record PerformanceBudgetViolation(
    string BenchmarkName,
    string Message,
    double? ActualMeanNanoseconds = null,
    double? MaxMeanNanoseconds = null,
    long? ActualAllocatedBytes = null,
    long? MaxAllocatedBytes = null
);

internal sealed class PerformanceBudgetResult(IReadOnlyList<PerformanceBudgetViolation> violations)
{
    public IReadOnlyList<PerformanceBudgetViolation> Violations { get; } =
        new ReadOnlyCollection<PerformanceBudgetViolation>(violations.ToArray());

    public bool Succeeded => Violations.Count == 0;

    public void WriteTo(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (Succeeded)
        {
            writer.WriteLine("Performance budgets passed.");
            return;
        }

        writer.WriteLine("Performance budget failures:");
        foreach (PerformanceBudgetViolation violation in Violations)
        {
            writer.WriteLine("  " + violation.Message);
        }
    }
}

internal static class PerformanceBudgetCatalog
{
    public static IReadOnlyDictionary<string, PerformanceBudget> Budgets { get; } =
        new ReadOnlyDictionary<string, PerformanceBudget>(
            new Dictionary<string, PerformanceBudget>(StringComparer.Ordinal)
            {
                ["WriteLengthEncodedRow"] = new(
                    "WriteLengthEncodedRow",
                    MaxMeanNanoseconds: 5_000,
                    MaxAllocatedBytes: 4_096
                ),
                ["ReadLengthEncodedString"] = new(
                    "ReadLengthEncodedString",
                    MaxMeanNanoseconds: 5_000,
                    MaxAllocatedBytes: 4_096
                ),
                ["ParseIntegerValue"] = new(
                    "ParseIntegerValue",
                    MaxMeanNanoseconds: 1_000,
                    MaxAllocatedBytes: 512
                ),
                ["ParseStringValue"] = new(
                    "ParseStringValue",
                    MaxMeanNanoseconds: 1_000,
                    MaxAllocatedBytes: 1_024
                ),
                ["FormatSqlLiteral"] = new(
                    "FormatSqlLiteral",
                    MaxMeanNanoseconds: 2_000,
                    MaxAllocatedBytes: 2_048
                ),
                // Eight cached lookups per op. Measured ~278 ns / 0 B (CLR) and ~553 ns / 176 B
                // (store) on a dev machine; ceilings keep headroom for slower CI runners while
                // still catching a real regression (e.g., loss of the FrozenDictionary fast path).
                ["FindClrTypeMapping"] = new(
                    "FindClrTypeMapping",
                    MaxMeanNanoseconds: 2_000,
                    MaxAllocatedBytes: 256
                ),
                ["FindStoreTypeMapping"] = new(
                    "FindStoreTypeMapping",
                    MaxMeanNanoseconds: 3_000,
                    MaxAllocatedBytes: 512
                ),
                // Packet framing read path. Budgets are set from measured values with headroom for
                // slower CI runners; they guard the per-row allocation hot path against regressions.
                // ~712 B measured with the single-packet fast path; the 1 KB ceiling guards that
                // path so a regression back to the buffered ArrayBufferWriter copy (~1.25 KB) fails.
                ["ReadSinglePacketPayload"] = new(
                    "ReadSinglePacketPayload",
                    MaxMeanNanoseconds: 5_000,
                    MaxAllocatedBytes: 1_024
                ),
                ["ReadMultiPacketPayload"] = new(
                    "ReadMultiPacketPayload",
                    MaxMeanNanoseconds: 20_000,
                    MaxAllocatedBytes: 16_384
                ),
                // Parameter binding for a repeatedly executed command. Budgets set from measured
                // values with CI headroom; the prepared variant should not regress past the
                // re-scan variant.
                ["BindParameterized"] = new(
                    "BindParameterized",
                    MaxMeanNanoseconds: 5_000,
                    MaxAllocatedBytes: 2_048
                ),
                ["BindPreparedParameterized"] = new(
                    "BindPreparedParameterized",
                    MaxMeanNanoseconds: 5_000,
                    MaxAllocatedBytes: 2_048
                ),
            }
        );
}

internal static class PerformanceBudgetValidator
{
    [SuppressMessage(
        "Globalization",
        "CA1303:Do not pass literals as localized parameters",
        Justification = "Benchmark budget output is developer-facing tooling text."
    )]
    public static PerformanceBudgetResult Validate(IEnumerable<Summary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        List<PerformanceBudgetViolation> reportViolations = [];
        List<PerformanceBudgetMeasurement> measurements = [];
        bool sawServerBackedReport = false;
        foreach (Summary summary in summaries)
        {
            foreach (BenchmarkReport report in summary.Reports)
            {
                string benchmarkName = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;

                // Server-backed benchmarks are observational and need a live server, so they are
                // never gated against the budget catalog.
                if (
                    report.BenchmarkCase.Descriptor.Categories.Contains(
                        BenchmarkCategories.ServerBacked,
                        StringComparer.Ordinal
                    )
                )
                {
                    sawServerBackedReport = true;
                    continue;
                }

                // Dry jobs only verify that a benchmark compiles and runs once; they carry no
                // statistically meaningful timing, so they are not counted as measurements.
                if (string.Equals(report.BenchmarkCase.Job.Id, "Dry", StringComparison.Ordinal))
                {
                    continue;
                }

                // A failed or statistics-less report must surface as a violation rather than be
                // silently dropped, otherwise a broken benchmark would pass the performance gate.
                if (!report.Success)
                {
                    reportViolations.Add(
                        new PerformanceBudgetViolation(
                            benchmarkName,
                            $"Benchmark '{benchmarkName}' failed to run; no measurement was produced."
                        )
                    );
                    continue;
                }

                if (report.ResultStatistics is null)
                {
                    reportViolations.Add(
                        new PerformanceBudgetViolation(
                            benchmarkName,
                            $"Benchmark '{benchmarkName}' produced no result statistics."
                        )
                    );
                    continue;
                }

                measurements.Add(
                    new PerformanceBudgetMeasurement(
                        benchmarkName,
                        report.ResultStatistics.Mean,
                        report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase)
                    )
                );
            }
        }

        // A server-backed-only run (no budgeted measurements, but server benchmarks did run) is
        // not a gate run, so it must not trip the empty-measurement guard below.
        if (measurements.Count == 0 && sawServerBackedReport)
        {
            return new PerformanceBudgetResult(reportViolations);
        }

        PerformanceBudgetResult budgetResult = Validate(
            measurements,
            PerformanceBudgetCatalog.Budgets
        );
        return new PerformanceBudgetResult([.. reportViolations, .. budgetResult.Violations]);
    }

    [SuppressMessage(
        "Globalization",
        "CA1303:Do not pass literals as localized parameters",
        Justification = "Benchmark budget output is developer-facing tooling text."
    )]
    internal static PerformanceBudgetResult Validate(
        IEnumerable<PerformanceBudgetMeasurement> measurements,
        IReadOnlyDictionary<string, PerformanceBudget> budgets
    )
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(budgets);

        List<PerformanceBudgetViolation> violations = [];
        int validatedCount = 0;
        foreach (PerformanceBudgetMeasurement measurement in measurements)
        {
            validatedCount++;
            if (!budgets.TryGetValue(measurement.BenchmarkName, out PerformanceBudget? budget))
            {
                violations.Add(
                    new PerformanceBudgetViolation(
                        measurement.BenchmarkName,
                        $"No performance budget is configured for benchmark '{measurement.BenchmarkName}'."
                    )
                );
                continue;
            }

            if (measurement.MeanNanoseconds > budget.MaxMeanNanoseconds)
            {
                violations.Add(
                    new PerformanceBudgetViolation(
                        measurement.BenchmarkName,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Benchmark '{measurement.BenchmarkName}' mean {measurement.MeanNanoseconds:N2} ns exceeds budget {budget.MaxMeanNanoseconds:N2} ns."
                        ),
                        measurement.MeanNanoseconds,
                        budget.MaxMeanNanoseconds
                    )
                );
            }

            if (measurement.AllocatedBytes is null)
            {
                violations.Add(
                    new PerformanceBudgetViolation(
                        measurement.BenchmarkName,
                        $"Benchmark '{measurement.BenchmarkName}' did not report allocated bytes. Ensure MemoryDiagnoser is enabled."
                    )
                );
                continue;
            }

            if (measurement.AllocatedBytes > budget.MaxAllocatedBytes)
            {
                violations.Add(
                    new PerformanceBudgetViolation(
                        measurement.BenchmarkName,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Benchmark '{measurement.BenchmarkName}' allocated {measurement.AllocatedBytes:N0} B/op exceeds budget {budget.MaxAllocatedBytes:N0} B/op."
                        ),
                        ActualAllocatedBytes: measurement.AllocatedBytes,
                        MaxAllocatedBytes: budget.MaxAllocatedBytes
                    )
                );
            }
        }

        // An empty measurement set must never pass: a typoed --filter, a Dry-only run, or
        // benchmarks that all failed would otherwise bypass the gate with a green result.
        if (validatedCount == 0)
        {
            violations.Add(
                new PerformanceBudgetViolation(
                    "(none)",
                    "No benchmark measurements were validated; a typoed --filter, a Dry-only run, or failed benchmarks must not pass the performance gate."
                )
            );
        }

        return new PerformanceBudgetResult(violations);
    }
}
