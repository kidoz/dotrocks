using DotRocks.Benchmarks;
using Xunit;

namespace DotRocks.Benchmarks.Tests;

public sealed class PerformanceBudgetValidatorTests
{
    [Fact]
    public void Validate_WhenMeasurementsAreWithinBudgets_Succeeds()
    {
        PerformanceBudgetResult result = PerformanceBudgetValidator.Validate(
            [
                new PerformanceBudgetMeasurement(
                    "WriteLengthEncodedRow",
                    MeanNanoseconds: 1_000,
                    AllocatedBytes: 512
                ),
            ],
            PerformanceBudgetCatalog.Budgets
        );

        Assert.True(result.Succeeded);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Validate_WhenMeanExceedsBudget_ReportsViolation()
    {
        PerformanceBudgetResult result = PerformanceBudgetValidator.Validate(
            [
                new PerformanceBudgetMeasurement(
                    "ParseIntegerValue",
                    MeanNanoseconds: 1_001,
                    AllocatedBytes: 64
                ),
            ],
            PerformanceBudgetCatalog.Budgets
        );

        PerformanceBudgetViolation violation = Assert.Single(result.Violations);
        Assert.False(result.Succeeded);
        Assert.Equal("ParseIntegerValue", violation.BenchmarkName);
        Assert.Equal(1_001, violation.ActualMeanNanoseconds);
        Assert.Equal(1_000, violation.MaxMeanNanoseconds);
        Assert.Contains("mean", violation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_WhenAllocationExceedsBudget_ReportsViolation()
    {
        PerformanceBudgetResult result = PerformanceBudgetValidator.Validate(
            [
                new PerformanceBudgetMeasurement(
                    "ParseStringValue",
                    MeanNanoseconds: 100,
                    AllocatedBytes: 1_025
                ),
            ],
            PerformanceBudgetCatalog.Budgets
        );

        PerformanceBudgetViolation violation = Assert.Single(result.Violations);
        Assert.False(result.Succeeded);
        Assert.Equal("ParseStringValue", violation.BenchmarkName);
        Assert.Equal(1_025, violation.ActualAllocatedBytes);
        Assert.Equal(1_024, violation.MaxAllocatedBytes);
        Assert.Contains("allocated", violation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_WhenBenchmarkHasNoBudget_ReportsViolation()
    {
        PerformanceBudgetResult result = PerformanceBudgetValidator.Validate(
            [
                new PerformanceBudgetMeasurement(
                    "NewBenchmarkWithoutBudget",
                    MeanNanoseconds: 1,
                    AllocatedBytes: 0
                ),
            ],
            PerformanceBudgetCatalog.Budgets
        );

        PerformanceBudgetViolation violation = Assert.Single(result.Violations);
        Assert.False(result.Succeeded);
        Assert.Equal("NewBenchmarkWithoutBudget", violation.BenchmarkName);
        Assert.Contains("No performance budget", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenAllocationMetricIsMissing_ReportsViolation()
    {
        PerformanceBudgetResult result = PerformanceBudgetValidator.Validate(
            [
                new PerformanceBudgetMeasurement(
                    "FormatSqlLiteral",
                    MeanNanoseconds: 100,
                    AllocatedBytes: null
                ),
            ],
            PerformanceBudgetCatalog.Budgets
        );

        PerformanceBudgetViolation violation = Assert.Single(result.Violations);
        Assert.False(result.Succeeded);
        Assert.Equal("FormatSqlLiteral", violation.BenchmarkName);
        Assert.Contains("MemoryDiagnoser", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenNoMeasurements_ReportsViolation()
    {
        PerformanceBudgetResult result = PerformanceBudgetValidator.Validate(
            [],
            PerformanceBudgetCatalog.Budgets
        );

        PerformanceBudgetViolation violation = Assert.Single(result.Violations);
        Assert.False(result.Succeeded);
        Assert.Contains(
            "No benchmark measurements were validated",
            violation.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ClassifyReport_WhenServerBackedBenchmarkFails_ReportsViolationBeforeSkippingBudget()
    {
        PerformanceReportDisposition disposition = PerformanceBudgetValidator.ClassifyReport(
            "StreamLargeResult",
            [BenchmarkCategories.ServerBacked],
            succeeded: false
        );

        Assert.True(disposition.IsServerBacked);
        Assert.NotNull(disposition.ExecutionViolation);
        Assert.Equal("StreamLargeResult", disposition.ExecutionViolation.BenchmarkName);
        Assert.Contains(
            "failed to run",
            disposition.ExecutionViolation.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void IsServerBackedOnlyRun_WhenLocalDryReportWasSeen_DoesNotBypassEmptyGuard()
    {
        Assert.False(
            PerformanceBudgetValidator.IsServerBackedOnlyRun(
                measurementCount: 0,
                sawServerBackedReport: true,
                sawNonServerBackedReport: true
            )
        );
        Assert.True(
            PerformanceBudgetValidator.IsServerBackedOnlyRun(
                measurementCount: 0,
                sawServerBackedReport: true,
                sawNonServerBackedReport: false
            )
        );
    }

    [Fact]
    public void BudgetCatalog_CoversEveryBudgetedBenchmarkInTheAssembly()
    {
        // Server-backed benchmarks are observational and intentionally have no budget, so they are
        // excluded from the coverage requirement (matching the validator).
        string[] benchmarkNames = typeof(SerializationBenchmarks)
            .Assembly.GetTypes()
            .SelectMany(type => type.GetMethods())
            .Where(method =>
                method
                    .GetCustomAttributes(inherit: false)
                    .Any(attribute => attribute.GetType().Name == "BenchmarkAttribute")
                && !IsServerBacked(method)
            )
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            benchmarkNames,
            PerformanceBudgetCatalog.Budgets.Keys.Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void StreamLoadBenchmark_ResetsTableBeforeEveryMeasuredIteration()
    {
        System.Reflection.MethodInfo? resetMethod = typeof(StreamLoadBenchmarks).GetMethod(
            nameof(StreamLoadBenchmarks.ResetTable)
        );

        Assert.NotNull(resetMethod);
        Assert.Contains(
            resetMethod.GetCustomAttributes(inherit: false),
            attribute => attribute.GetType().Name == "IterationSetupAttribute"
        );
    }

    private static bool IsServerBacked(System.Reflection.MethodInfo method)
    {
        // BenchmarkCategory may be declared on the method or on its containing class.
        return HasServerBackedCategory(method.GetCustomAttributes(inherit: false))
            || (
                method.DeclaringType is not null
                && HasServerBackedCategory(method.DeclaringType.GetCustomAttributes(inherit: false))
            );
    }

    private static bool HasServerBackedCategory(object[] attributes)
    {
        foreach (object attribute in attributes)
        {
            if (attribute.GetType().Name != "BenchmarkCategoryAttribute")
            {
                continue;
            }

            if (
                attribute.GetType().GetProperty("Categories")?.GetValue(attribute)
                    is string[] categories
                && categories.Contains("ServerBacked", StringComparer.Ordinal)
            )
            {
                return true;
            }
        }

        return false;
    }
}
