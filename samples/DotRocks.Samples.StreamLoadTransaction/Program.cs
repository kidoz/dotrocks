using System.Text;
using DotRocks.Data.Loading;

// Two-phase Stream Load transaction: begin a labeled transaction, stream one or more loads into
// it, prepare (server-side pre-commit), then commit — or roll back to discard everything. This
// gives all-or-nothing bulk ingestion across multiple load calls under a single idempotent label.
string connectionString =
    Environment.GetEnvironmentVariable("DOTROCKS_CONNECTION_STRING")
    ?? "Server=127.0.0.1;Port=9030;User ID=root;Stream Load Endpoint=https://127.0.0.1:8030";

using var client = new DotRocksStreamLoadClient(connectionString);

DotRocksStreamLoadTransaction transaction = await client.BeginTransactionAsync(
    databaseName: "dotrocks_sample",
    tableName: "events",
    options: new DotRocksStreamLoadTransactionOptions
    {
        Label = $"ingest-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}",
    }
);

try
{
    // Multiple loads accumulate under the one transaction/label.
    await using var firstBatch = new MemoryStream(Encoding.UTF8.GetBytes("1,login\n2,logout\n"));
    await transaction.LoadCsvAsync(
        firstBatch,
        new DotRocksStreamLoadOptions { Columns = "id,event_name" }
    );

    await using var secondBatch = new MemoryStream(Encoding.UTF8.GetBytes("3,login\n"));
    await transaction.LoadCsvAsync(
        secondBatch,
        new DotRocksStreamLoadOptions { Columns = "id,event_name" }
    );

    // Prepare pre-commits server-side; Commit then makes every loaded row visible atomically.
    await transaction.PrepareAsync();
    DotRocksStreamLoadResult commit = await transaction.CommitAsync();
    Console.WriteLine($"Committed transaction {transaction.Label}: {commit.Status}.");
}
catch
{
    // Any failure discards the whole label so no partial data is committed.
    await transaction.RollbackAsync();
    Console.WriteLine($"Rolled back transaction {transaction.Label}.");
    throw;
}
