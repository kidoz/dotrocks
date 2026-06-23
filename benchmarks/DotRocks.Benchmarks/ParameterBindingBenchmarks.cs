using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using DotRocks.Data;
using DotRocks.Data.Protocol.Commands;

namespace DotRocks.Benchmarks;

/// <summary>
/// Benchmarks parameter binding for a repeatedly executed command, comparing a fresh
/// command-text scan (<see cref="CommandTextParameterBinder.Bind"/>) against binding from a cached
/// tokenized template (<see cref="CommandTextParameterBinder.BindPrepared"/>).
/// </summary>
[MemoryDiagnoser]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "BenchmarkDotNet owns the lifecycle; the command is disposed in [GlobalCleanup]."
)]
public class ParameterBindingBenchmarks
{
    private const string Sql =
        "SELECT id, name FROM warehouse /* region filter */ WHERE region = @region "
        + "AND active = @active AND name LIKE 'a%%' AND note <> '@notparam' ORDER BY id";

    private DotRocksCommand _command = null!;
    private PreparedCommandText _prepared = null!;

    [GlobalSetup]
    public void Setup()
    {
        _command = new DotRocksCommand { CommandText = Sql };
        _command.Parameters.Add(new DotRocksParameter { ParameterName = "region", Value = "EU" });
        _command.Parameters.Add(new DotRocksParameter { ParameterName = "active", Value = true });
        _prepared = CommandTextParameterBinder.Prepare(Sql, _command.Parameters);
    }

    [GlobalCleanup]
    public void Cleanup() => _command.Dispose();

    [Benchmark(Baseline = true)]
    public string BindParameterized() =>
        CommandTextParameterBinder.Bind(Sql, _command.Parameters);

    [Benchmark]
    public string BindPreparedParameterized() =>
        CommandTextParameterBinder.BindPrepared(_prepared, _command.Parameters);
}
