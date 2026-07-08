using System.Globalization;
using DotRocks.Data;

// Connection pooling & resilience sample: reuse physical connections across logical opens and
// retry transient open failures. Pooling is off by default; enable and size it on the data source.
var builder = new DotRocksConnectionStringBuilder(
    Environment.GetEnvironmentVariable("DOTROCKS_CONNECTION_STRING")
        ?? "Server=127.0.0.1;Port=9030;User ID=root;Database=dotrocks_sample"
)
{
    Pooling = true,
    MinimumPoolSize = 1, // keep one warm connection ready
    MaximumPoolSize = 20, // cap concurrent physical connections
    ConnectionIdleTimeout = 60, // retire a connection idle for 60s
    ConnectionLifetime = 300, // recycle a connection after ~5 min (jittered) to spread reconnects
    ConnectionRetries = 3, // retry a transient open failure up to 3 times...
    ConnectionRetryDelay = 200, // ...waiting 200 ms between attempts
};

await using var dataSource = new DotRocksDataSource(builder.ConnectionString);

// Open, use, and close twice. The second open reuses the first physical connection from the
// pool (same server-side connection id) instead of establishing a new one.
long firstId = await ReadConnectionIdAsync(dataSource);
long secondId = await ReadConnectionIdAsync(dataSource);

Console.WriteLine($"First open  -> connection {firstId}");
Console.WriteLine($"Second open -> connection {secondId} (reused: {firstId == secondId})");

static async Task<long> ReadConnectionIdAsync(DotRocksDataSource dataSource)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT CONNECTION_ID()";
    object? id = await command.ExecuteScalarAsync();
    return Convert.ToInt64(id, CultureInfo.InvariantCulture);
}
