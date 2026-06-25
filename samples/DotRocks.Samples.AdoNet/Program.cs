using DotRocks.Data;

// Minimal ADO.NET sample: open a pooled connection from a data source, run a
// parameterized query, and stream the result without buffering the whole set.
string connectionString =
    Environment.GetEnvironmentVariable("DOTROCKS_CONNECTION_STRING")
    ?? "Server=127.0.0.1;Port=9030;User ID=root;Database=dotrocks_sample";

await using var dataSource = new DotRocksDataSource(connectionString);
await using DotRocksConnection connection = (DotRocksConnection)
    await dataSource.OpenConnectionAsync();

await using var command = connection.CreateCommand();
command.CommandText = "SELECT event_time, event_name FROM events WHERE tenant_id = @tenant";
command.Parameters.Add(new DotRocksParameter { ParameterName = "@tenant", Value = 42 });

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    // Read values column-by-column; the reader does not buffer the full result set.
    Console.WriteLine($"{reader.GetValue(0)} {reader.GetValue(1)}");
}
