using System.Diagnostics.CodeAnalysis;
using System.Text;
using BenchmarkDotNet.Attributes;
using DotRocks.Data.Loading;

namespace DotRocks.Benchmarks;

/// <summary>Server-backed benchmark for streamed CSV upload throughput.</summary>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.ServerBacked)]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "BenchmarkDotNet owns the lifecycle; the client is disposed in [GlobalCleanup]."
)]
public class StreamLoadBenchmarks
{
    private const string TableName = "stream_load_rows";

    private byte[] _payload = [];
    private DotRocksStreamLoadClient _client = null!;
    private string _connectionString = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _connectionString = BenchmarkServer.EnsureDatabase();
        BenchmarkServer.Execute(_connectionString, $"DROP TABLE IF EXISTS {TableName}");
        BenchmarkServer.Execute(
            _connectionString,
            $"CREATE TABLE {TableName} (`id` BIGINT NOT NULL, `value` VARCHAR(64) NOT NULL) "
                + "DUPLICATE KEY (`id`) DISTRIBUTED BY HASH (`id`) BUCKETS 1 "
                + "PROPERTIES (\"replication_num\" = \"1\")"
        );

        var csv = new StringBuilder();
        for (int i = 0; i < 10_000; i++)
        {
            csv.Append(i).Append(',').Append("value-").Append(i).Append('\n');
        }

        _payload = Encoding.UTF8.GetBytes(csv.ToString());
        _client = new DotRocksStreamLoadClient(_connectionString);
    }

    [IterationSetup]
    public void ResetTable() =>
        BenchmarkServer.Execute(_connectionString, $"TRUNCATE TABLE {TableName}");

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        BenchmarkServer.Execute(_connectionString, $"DROP TABLE IF EXISTS {TableName}");
    }

    // IterationSetup makes BenchmarkDotNet use one invocation and an unroll factor of one, so each
    // measured iteration uploads exactly one payload into a freshly truncated table.
    [Benchmark]
    public async Task<long> StreamCsvPayload()
    {
        using var payload = new MemoryStream(_payload, writable: false);
        DotRocksStreamLoadResult result = await _client.LoadCsvAsync(
            BenchmarkServer.Database,
            TableName,
            payload
        );
        return result.LoadBytes;
    }
}
