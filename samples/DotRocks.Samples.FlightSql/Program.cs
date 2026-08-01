using DotRocks.FlightSql;

string endpoint =
    Environment.GetEnvironmentVariable("DOTROCKS_FLIGHT_SQL_ENDPOINT") ?? "grpc://127.0.0.1:9408";
string userName = Environment.GetEnvironmentVariable("DOTROCKS_USER") ?? "root";
string password = Environment.GetEnvironmentVariable("DOTROCKS_PASSWORD") ?? string.Empty;

var options = new DotRocksFlightSqlOptions(new Uri(endpoint), userName, password)
{
    // StarRocks commonly exposes a plaintext grpc:// endpoint. Limit this to local development or
    // a trusted private network; credentials and results are otherwise visible on the wire.
    AllowInsecureTransport = endpoint.StartsWith("grpc://", StringComparison.OrdinalIgnoreCase),
};

// Dispose asynchronously: the data source releases its StarRocks session on the wire.
await using var dataSource = new DotRocksFlightSqlDataSource(options);

DotRocksFlightSqlResult result = await dataSource.ExecuteQueryAsync("SELECT 1 AS value");
Console.WriteLine(result.Schema);

await foreach (var batch in result.ReadRecordBatchesAsync())
{
    using (batch)
    {
        Console.WriteLine($"Received {batch.Length} row(s) in {batch.ColumnCount} column(s).");
    }
}

// Sharing the data source reuses its channel and authenticated session.
await using DotRocksFlightSqlDbConnection connection = dataSource.CreateConnection();
await connection.OpenAsync();
await using DotRocksFlightSqlCommand command = connection.CreateCommand();
command.CommandText = "SELECT @value AS value";
command.Parameters.Add(new DotRocks.Data.DotRocksParameter { ParameterName = "value", Value = 42 });
Console.WriteLine($"ADO.NET scalar: {await command.ExecuteScalarAsync()}");
