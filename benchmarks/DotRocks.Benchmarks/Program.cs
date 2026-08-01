using BenchmarkDotNet.Running;
using DotRocks.Benchmarks;

IEnumerable<BenchmarkDotNet.Reports.Summary> summaries = BenchmarkSwitcher
    .FromAssembly(typeof(Program).Assembly)
    .Run(args, BenchmarkDotNet.Configs.DefaultConfig.Instance);

// Mean-time budgets are calibrated on developer hardware and do not transfer to shared CI
// runners, so CI sets DOTROCKS_BENCHMARK_ALLOCATION_ONLY=1 to enforce the machine-independent
// allocation budgets only. The report states when timing was not checked.
bool allocationOnly = string.Equals(
    Environment.GetEnvironmentVariable("DOTROCKS_BENCHMARK_ALLOCATION_ONLY"),
    "1",
    StringComparison.Ordinal
);

PerformanceBudgetResult budgetResult = PerformanceBudgetValidator.Validate(
    summaries,
    enforceMeanBudgets: !allocationOnly
);
budgetResult.WriteTo(Console.Error);
return budgetResult.Succeeded ? 0 : 1;

internal sealed partial class Program;
