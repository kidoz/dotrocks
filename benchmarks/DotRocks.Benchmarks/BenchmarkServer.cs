using System.Diagnostics.CodeAnalysis;
using DotRocks.Data;

namespace DotRocks.Benchmarks;

/// <summary>
/// Shared connection setup for server-backed benchmarks. The connection string comes from the
/// <c>DOTROCKS_BENCH_CONNECTION_STRING</c> environment variable, defaulting to a local Docker
/// StarRocks with pooling enabled.
/// </summary>
internal static class BenchmarkServer
{
    public const string Database = "dotrocks_bench";

    public static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("DOTROCKS_BENCH_CONNECTION_STRING")
        ?? "Server=127.0.0.1;Port=9030;User ID=root;Pooling=true";

    /// <summary>
    /// Ensures the benchmark database exists and returns a connection string scoped to it.
    /// </summary>
    public static string EnsureDatabase()
    {
        Execute(BaseConnectionString, $"CREATE DATABASE IF NOT EXISTS {Database}");
        return BaseConnectionString + ";Database=" + Database;
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Benchmark setup SQL is fixed, benchmark-controlled text, not user input."
    )]
    public static void Execute(string connectionString, string sql)
    {
        using var connection = new DotRocksConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
