using Dapper;
using DotRocks.Data;

// Dapper works over the DotRocks ADO.NET surface: open a DotRocksConnection and use the
// standard Dapper extension methods with named parameters.
string connectionString =
    Environment.GetEnvironmentVariable("DOTROCKS_CONNECTION_STRING")
    ?? "Server=127.0.0.1;Port=9030;User ID=root;Database=dotrocks_sample";

await using var connection = new DotRocksConnection(connectionString);
await connection.OpenAsync();

IEnumerable<EventRow> events = await connection.QueryAsync<EventRow>(
    "SELECT event_name AS Name, event_time AS OccurredAt FROM events WHERE tenant_id = @Tenant",
    new { Tenant = 42 }
);

foreach (EventRow row in events)
{
    Console.WriteLine($"{row.OccurredAt:o} {row.Name}");
}

internal sealed record EventRow(string Name, DateTime OccurredAt);
