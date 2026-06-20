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
            }
        );
}

internal static class PerformanceBudgetValidator
{
    public static PerformanceBudgetResult Validate(IEnumerable<Summary> summaries) =>
        Validate(ExtractMeasurements(summaries), PerformanceBudgetCatalog.Budgets);

    internal static PerformanceBudgetResult Validate(
        IEnumerable<PerformanceBudgetMeasurement> measurements,
        IReadOnlyDictionary<string, PerformanceBudget> budgets
    )
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(budgets);

        List<PerformanceBudgetViolation> violations = [];
        foreach (PerformanceBudgetMeasurement measurement in measurements)
        {
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

        return new PerformanceBudgetResult(violations);
    }

    [SuppressMessage(
        "Globalization",
        "CA1303:Do not pass literals as localized parameters",
        Justification = "Benchmark budget output is developer-facing tooling text."
    )]
    private static IEnumerable<PerformanceBudgetMeasurement> ExtractMeasurements(
        IEnumerable<Summary> summaries
    )
    {
        ArgumentNullException.ThrowIfNull(summaries);

        foreach (Summary summary in summaries)
        {
            foreach (BenchmarkReport report in summary.Reports)
            {
                if (!report.Success || report.ResultStatistics is null)
                {
                    continue;
                }

                if (string.Equals(report.BenchmarkCase.Job.Id, "Dry", StringComparison.Ordinal))
                {
                    continue;
                }

                string benchmarkName = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
                yield return new PerformanceBudgetMeasurement(
                    benchmarkName,
                    report.ResultStatistics.Mean,
                    report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase)
                );
            }
        }
    }
}
