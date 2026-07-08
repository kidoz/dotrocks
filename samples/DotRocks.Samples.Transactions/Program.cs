using DotRocks.Data;

// Transaction sample: run statements atomically. DotRocks issues BEGIN/COMMIT/ROLLBACK; only
// ReadCommitted (or Unspecified, which maps to it) isolation is accepted, and disposing an
// uncommitted transaction rolls it back.
string connectionString =
    Environment.GetEnvironmentVariable("DOTROCKS_CONNECTION_STRING")
    ?? "Server=127.0.0.1;Port=9030;User ID=root;Database=dotrocks_sample";

await using var connection = new DotRocksConnection(connectionString);
await connection.OpenAsync();

// Commit path: the row is persisted only after CommitAsync succeeds.
await using (var transaction = await connection.BeginTransactionAsync())
{
    await using var insert = connection.CreateCommand();
    insert.Transaction = transaction;
    insert.CommandText = "INSERT INTO events (id, event_name) VALUES (@id, @name)";
    insert.Parameters.Add(new DotRocksParameter { ParameterName = "@id", Value = 1 });
    insert.Parameters.Add(new DotRocksParameter { ParameterName = "@name", Value = "login" });
    int inserted = await insert.ExecuteNonQueryAsync();

    await transaction.CommitAsync();
    Console.WriteLine($"Committed {inserted} row(s).");
}

// Rollback path: RollbackAsync discards the transaction's work. Simply disposing an
// uncommitted transaction (leaving this block) issues the same ROLLBACK.
await using (var transaction = await connection.BeginTransactionAsync())
{
    await using var insert = connection.CreateCommand();
    insert.Transaction = transaction;
    insert.CommandText = "INSERT INTO events (id, event_name) VALUES (2, 'logout')";
    int inserted = await insert.ExecuteNonQueryAsync();

    await transaction.RollbackAsync();
    Console.WriteLine($"Rolled back {inserted} row(s).");
}
