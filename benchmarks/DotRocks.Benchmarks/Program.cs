using BenchmarkDotNet.Running;
using DotRocks.Benchmarks;

IEnumerable<BenchmarkDotNet.Reports.Summary> summaries = BenchmarkSwitcher
    .FromAssembly(typeof(Program).Assembly)
    .Run(args, BenchmarkDotNet.Configs.DefaultConfig.Instance);

PerformanceBudgetResult budgetResult = PerformanceBudgetValidator.Validate(summaries);
budgetResult.WriteTo(Console.Error);
return budgetResult.Succeeded ? 0 : 1;

internal sealed partial class Program;
