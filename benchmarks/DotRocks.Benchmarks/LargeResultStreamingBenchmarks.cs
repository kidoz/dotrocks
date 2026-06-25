using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using DotRocks.Data;

namespace DotRocks.Benchmarks;

/// <summary>
/// Server-backed large-result streaming benchmark: reads a multi-thousand-row result set through
/// the streaming reader to measure time-to-drain and per-row allocation. Requires a live StarRocks
/// server (see <see cref="BenchmarkServer"/>); excluded from the performance budget gate.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.ServerBacked)]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
public class LargeResultStreamingBenchmarks
{
    private const int RowCount = 10_000;
    private const int InsertChunk = 2_000;

    private string _connectionString = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _connectionString = BenchmarkServer.EnsureDatabase();
        BenchmarkServer.Execute(_connectionString, "DROP TABLE IF EXISTS large_rows");
        BenchmarkServer.Execute(
            _connectionString,
            "CREATE TABLE large_rows (`id` BIGINT NOT NULL, `value` VARCHAR(64) NOT NULL, "
                + "`amount` DECIMAL(18,4) NOT NULL) DUPLICATE KEY (`id`) "
                + "DISTRIBUTED BY HASH (`id`) BUCKETS 1 PROPERTIES (\"replication_num\" = \"1\")"
        );

        for (int start = 0; start < RowCount; start += InsertChunk)
        {
            int end = Math.Min(start + InsertChunk, RowCount);
            var insert = new StringBuilder("INSERT INTO large_rows VALUES ");
            for (int id = start; id < end; id++)
            {
                if (id > start)
                {
                    insert.Append(',');
                }

                insert.Append(
                    CultureInfo.InvariantCulture,
                    $"({id}, 'row-{id}', {id}.{id % 10000:D4})"
                );
            }

            BenchmarkServer.Execute(_connectionString, insert.ToString());
        }
    }

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public async Task<long> StreamLargeResult()
    {
        await using var connection = new DotRocksConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, value, amount FROM large_rows";

        long checksum = 0;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            checksum += reader.GetInt64(0);
            _ = reader.GetString(1);
            _ = reader.GetDecimal(2);
        }

        return checksum;
    }
}
