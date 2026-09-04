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
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "BenchmarkDotNet calls GlobalCleanup to dispose the owned connections and data source."
)]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
public class FlightSqlTransportBenchmarks
{
    private const int RowCount = 10_000;
    private const int InsertChunk = 2_000;
    private const string RowQuery =
        $"SELECT id, value FROM `{BenchmarkServer.Database}`.`flight_rows`";
    private string _connectionString = string.Empty;
    private DotRocksFlightSqlDataSource _flightDataSource = null!;
    private DotRocksFlightSqlDbConnection _flightConnection = null!;
    private DotRocksConnection _mysqlConnection = null!;

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
        Uri[] allowedEndpoints = (
            Environment.GetEnvironmentVariable("DOTROCKS_BENCH_FLIGHT_ALLOWED_ENDPOINTS")
            ?? string.Empty
        )
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => new Uri(value, UriKind.Absolute))
            .ToArray();
        var flightOptions = new DotRocksFlightSqlOptions(
            new Uri(endpoint),
            Environment.GetEnvironmentVariable("DOTROCKS_BENCH_USER") ?? "root",
            Environment.GetEnvironmentVariable("DOTROCKS_BENCH_PASSWORD") ?? string.Empty
        )
        {
            AllowInsecureTransport = endpoint.StartsWith("grpc://", StringComparison.Ordinal),
            AllowedEndpointAddresses = allowedEndpoints,
            CommandTimeout = TimeSpan.FromMinutes(2),
        };

        // Connections are established once so that every iteration measures transport throughput
        // instead of connection and session setup.
        _flightDataSource = new DotRocksFlightSqlDataSource(flightOptions);
        _flightConnection = _flightDataSource.CreateConnection();
        _flightConnection.Open();
        _mysqlConnection = new DotRocksConnection(_connectionString);
        _mysqlConnection.Open();
    }

    [Benchmark(Baseline = true)]
    public async Task<long> MySqlProtocolRows()
    {
        await using DbCommand command = _mysqlConnection.CreateCommand();
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
        await using DotRocksFlightSqlCommand command = _flightConnection.CreateCommand();
        command.CommandText = RowQuery;
        await using DbDataReader reader = await command.ExecuteReaderAsync();

        long checksum = 0;
        while (await reader.ReadAsync())
        {
            checksum += reader.GetInt64(0);
            _ = reader.GetString(1);
        }

        return checksum;
    }

    /// <summary>
    /// Consumes the same columns as the row benchmarks, so the difference measures only row
    /// materialization rather than a smaller projection.
    /// </summary>
    [Benchmark]
    public async Task<long> FlightSqlRecordBatches()
    {
        DotRocksFlightSqlResult result = await _flightDataSource.ExecuteQueryAsync(RowQuery);
        long checksum = 0;
        await foreach (RecordBatch batch in result.ReadRecordBatchesAsync())
        {
            using (batch)
            {
                var ids = (Int64Array)batch.Column(0);
                var values = (StringArray)batch.Column(1);
                for (int index = 0; index < ids.Length; index++)
                {
                    checksum += ids.GetValue(index)!.Value;
                    _ = values.GetString(index);
                }
            }
        }

        return checksum;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _flightConnection.Dispose();
        _flightDataSource.Dispose();
        _mysqlConnection.Dispose();
    }
}
