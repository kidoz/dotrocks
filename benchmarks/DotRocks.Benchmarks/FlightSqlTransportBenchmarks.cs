using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Apache.Arrow;
using BenchmarkDotNet.Attributes;
using DotRocks.Data;
using DotRocks.FlightSql;

namespace DotRocks.Benchmarks;

/// <summary>
/// Compares MySQL-protocol row materialization, Flight SQL row materialization, and direct Arrow
/// record-batch consumption against the same live StarRocks result set.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.ServerBacked)]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
public class FlightSqlTransportBenchmarks
{
    private const int RowCount = 10_000;
    private const int InsertChunk = 2_000;
    private string _connectionString = string.Empty;
    private DotRocksFlightSqlOptions _flightOptions = null!;

    [GlobalSetup]
    public void Setup()
    {
        _connectionString = BenchmarkServer.EnsureDatabase();
        BenchmarkServer.Execute(_connectionString, "DROP TABLE IF EXISTS flight_rows");
        BenchmarkServer.Execute(
            _connectionString,
            "CREATE TABLE flight_rows (`id` BIGINT NOT NULL, `value` VARCHAR(64) NOT NULL) "
                + "DUPLICATE KEY (`id`) DISTRIBUTED BY HASH (`id`) BUCKETS 1 "
                + "PROPERTIES (\"replication_num\" = \"1\")"
        );
        for (int start = 0; start < RowCount; start += InsertChunk)
        {
            int end = Math.Min(start + InsertChunk, RowCount);
            var insert = new StringBuilder("INSERT INTO flight_rows VALUES ");
            for (int id = start; id < end; id++)
            {
                if (id > start)
                {
                    insert.Append(',');
                }

                insert.Append(CultureInfo.InvariantCulture, $"({id}, 'row-{id}')");
            }

            BenchmarkServer.Execute(_connectionString, insert.ToString());
        }

        string endpoint =
            Environment.GetEnvironmentVariable("DOTROCKS_BENCH_FLIGHT_ENDPOINT")
            ?? "grpc://127.0.0.1:9408";
        string[] allowedHosts = (
            Environment.GetEnvironmentVariable("DOTROCKS_BENCH_FLIGHT_ALLOWED_HOSTS")
            ?? string.Empty
        ).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _flightOptions = new DotRocksFlightSqlOptions(
            new Uri(endpoint),
            Environment.GetEnvironmentVariable("DOTROCKS_BENCH_USER") ?? "root",
            Environment.GetEnvironmentVariable("DOTROCKS_BENCH_PASSWORD") ?? string.Empty
        )
        {
            AllowInsecureTransport = endpoint.StartsWith("grpc://", StringComparison.Ordinal),
            AllowedEndpointHosts = allowedHosts,
            CommandTimeout = TimeSpan.FromMinutes(2),
        };
    }

    [Benchmark(Baseline = true)]
    public async Task<long> MySqlProtocolRows()
    {
        await using var connection = new DotRocksConnection(_connectionString);
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, value FROM flight_rows";
        await using DbDataReader reader = await command.ExecuteReaderAsync();

        long checksum = 0;
        while (await reader.ReadAsync())
        {
            checksum += reader.GetInt64(0);
            _ = reader.GetString(1);
        }

        return checksum;
    }

    [Benchmark]
    public async Task<long> FlightSqlRows()
    {
        await using var connection = new DotRocksFlightSqlDbConnection(_flightOptions);
        await connection.OpenAsync();
        await using DotRocksFlightSqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT id, value FROM `{BenchmarkServer.Database}`.`flight_rows`";
        await using DbDataReader reader = await command.ExecuteReaderAsync();

        long checksum = 0;
        while (await reader.ReadAsync())
        {
            checksum += reader.GetInt64(0);
            _ = reader.GetString(1);
        }

        return checksum;
    }

    [Benchmark]
    public async Task<long> FlightSqlRecordBatches()
    {
        using var dataSource = new DotRocksFlightSqlDataSource(_flightOptions);
        DotRocksFlightSqlResult result = await dataSource.ExecuteQueryAsync(
            $"SELECT id FROM `{BenchmarkServer.Database}`.`flight_rows`"
        );
        long checksum = 0;
        await foreach (RecordBatch batch in result.ReadRecordBatchesAsync())
        {
            using (batch)
            {
                var ids = (Int64Array)batch.Column(0);
                for (int index = 0; index < ids.Length; index++)
                {
                    checksum += ids.GetValue(index)!.Value;
                }
            }
        }

        return checksum;
    }
}
