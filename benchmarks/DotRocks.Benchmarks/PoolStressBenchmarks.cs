using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using DotRocks.Data;

namespace DotRocks.Benchmarks;

/// <summary>
/// Server-backed pool stress benchmarks: warm-pool lease/return latency, lease contention under
/// concurrency, and the cancellation-discard path. Requires a live StarRocks server (see
/// <see cref="BenchmarkServer"/>); excluded from the performance budget gate.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.ServerBacked)]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
public class PoolStressBenchmarks
{
    private string _connectionString = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _connectionString = BenchmarkServer.EnsureDatabase();

        // Warm the pool so lease benchmarks measure reuse rather than first-open cost.
        using var connection = new DotRocksConnection(_connectionString);
        connection.Open();
        connection.Close();
    }

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public async Task<object?> LeaseAndReturn()
    {
        await using var connection = new DotRocksConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        return await command.ExecuteScalarAsync();
    }

    [Benchmark]
    [Arguments(8)]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public async Task ConcurrentLease(int concurrency)
    {
        var leases = new Task[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            leases[i] = LeaseOnceAsync();
        }

        await Task.WhenAll(leases);
    }

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public async Task CancelledExecuteDiscard()
    {
        await using var connection = new DotRocksConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        try
        {
            await command.ExecuteScalarAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the cancelled connection is discarded on close rather than pooled.
        }
    }

    private async Task LeaseOnceAsync()
    {
        await using var connection = new DotRocksConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync();
    }
}
