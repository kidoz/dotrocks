# Arrow Flight SQL

`DotRocks.FlightSql` is an experimental alternative transport for StarRocks. It preserves Arrow
record batches for bulk analytics, and it also exposes an asynchronous ADO.NET surface that can be
passed to the existing DotRocks EF Core provider. `DotRocks.Data` remains the default, mature
MySQL-protocol provider.

StarRocks supports Arrow Flight SQL from 3.5.1, but its public guidance centers on data unloading.
Treat this package primarily as a read transport. Write and transaction support varies more by
server/client version than reads, so validate those operations against every StarRocks version in
your deployment before using them in production.

## Server configuration

Set a different `arrow_flight_port` in both `fe.conf` and `be.conf`, then restart the services. For
example, use port `9408` on the FE and `9419` on each BE/CN. FE proxy mode can route result batches
through the FE; without it, the BE/CN hosts and ports returned in `FlightInfo` must be reachable.

StarRocks commonly documents a plaintext `grpc://` endpoint. Plaintext exposes the Basic
authorization header, SQL, and results to network observers. Use it only for local development or
on a trusted private network. DotRocks rejects plaintext unless `AllowInsecureTransport` is set.
TLS endpoints use platform certificate validation with offline certificate-revocation checks,
matching the default posture of `DotRocks.Data`.

## Direct Arrow record batches

```csharp
var options = new DotRocksFlightSqlOptions(
    new Uri("grpc://127.0.0.1:9408"),
    "root",
    Environment.GetEnvironmentVariable("STARROCKS_PASSWORD") ?? string.Empty)
{
    AllowInsecureTransport = true,
    // Required only when FlightInfo points at different BE/CN addresses.
    AllowedEndpointAddresses =
    [
        new Uri("grpc+tls://starrocks-be-1.internal:9419"),
        new Uri("grpc+tls://starrocks-be-2.internal:9419"),
    ],
};

using var dataSource = new DotRocksFlightSqlDataSource(options);
DotRocksFlightSqlResult result = await dataSource.ExecuteQueryAsync(
    "SELECT event_name, event_count FROM analytics.events");

await foreach (Apache.Arrow.RecordBatch batch in result.ReadRecordBatchesAsync())
{
    using (batch)
    {
        Console.WriteLine($"{batch.Length} rows");
    }
}
```

The data source reuses gRPC channels per validated endpoint. A result is single-use and processes
Flight endpoints sequentially, preserving endpoint order without buffering the complete result.
When an endpoint advertises several locations, the trusted alternatives are tried in the order the
server supplied them. Dispose each record batch after consuming it.

## Sessions and disposal

StarRocks creates a frontend session for every authenticated call. DotRocks therefore exchanges the
credentials once, during the Flight handshake, for a session bearer token that all later calls on
that channel reuse, and releases the session with the Flight SQL `CloseSession` action on disposal.
Servers that do not implement the handshake fall back to per-call Basic authentication.

Two consequences matter in application code:

- Share a `DotRocksFlightSqlDataSource`. One data source holds one session per endpoint;
  `CreateConnection()` hands out ADO.NET connections that reuse it, whereas constructing
  `DotRocksFlightSqlDbConnection` from options gives each connection a private channel and session.
- Prefer `await using` (`IAsyncDisposable`) over `using`. Releasing the server session is a network
  call; the synchronous `Dispose` blocks on it with a five-second bound, and a session that is
  never released stays on the server until it expires.

```csharp
await using var dataSource = new DotRocksFlightSqlDataSource(options);

await using DotRocksFlightSqlDbConnection connection = dataSource.CreateConnection();
await connection.OpenAsync(cancellationToken);
```

`ExecuteUpdateAsync` sends the standard Flight SQL `CommandStatementUpdate` through `DoPut` and
returns the `DoPutUpdateResult` row count. Low-level Flight transactions are available through
`BeginTransactionAsync`; servers may reject them when their Flight endpoint does not advertise or
implement transaction actions. In live validation, the StarRocks 4.0.7 Flight endpoint returns
`UNIMPLEMENTED` for statement `DoPut`, so use the explicit MySQL write fallback described below for
that server line.

## Asynchronous ADO.NET

```csharp
await using var connection = new DotRocksFlightSqlDbConnection(options);
await connection.OpenAsync(cancellationToken);

await using DotRocksFlightSqlCommand command = connection.CreateCommand();
command.CommandText =
    "SELECT event_name, event_count FROM analytics.events WHERE tenant_id = @tenant";
command.Parameters.Add(new DotRocks.Data.DotRocksParameter
{
    ParameterName = "tenant",
    Value = tenantId,
});

await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
{
    Console.WriteLine($"{reader.GetString(0)}: {reader.GetInt64(1)}");
}
```

The ADO.NET surface intentionally supports asynchronous execution only. `ExecuteReader`,
`ExecuteScalar`, `ExecuteNonQuery`, synchronous transaction creation, and synchronous transaction
completion throw `NotSupportedException`; use their async counterparts. `Open()` only changes the
logical connection state and performs no network I/O: the session is authenticated on first
execution, so a configured MySQL-protocol fallback can still take over when the Flight endpoint is
unreachable.

`ExecuteReaderAsync` fetches the first record batch before returning, so `HasRows` reflects the
real result even though StarRocks does not declare a record count.

`CommandTimeout`, the token passed to `ExecuteReaderAsync`, and `Cancel()` remain active through
result streaming until the reader is exhausted or disposed. A token passed to any later
`ReadAsync` call can cancel the remaining stream. Cancellation surfaces as
`OperationCanceledException`, not as a gRPC `RpcException`. `CloseAsync` rolls back an active
Flight transaction asynchronously before closing the logical connection.

A Flight transaction is marked completed only once the server confirms it, so a `CommitAsync` or
`RollbackAsync` that fails leaves the transaction active and lets the caller retry or roll back.

StarRocks decimal columns declared with more precision than `System.Decimal` can represent (up to
`DECIMAL(38, s)`) are reported and materialized as `DotRocks.Data.DotRocksDecimal`. `GetDecimal`
still converts such a value when it fits, and throws `DotRocksPrecisionLossException` when it does
not.

Named `@parameter` values are validated and escaped with the same binder used by `DotRocks.Data`.
They are sent as SQL literals because the current transport does not expose server-prepared bind
batches. Output parameters and positional `?` placeholders are not supported.

## EF Core queries and optional writes

The existing provider accepts any `DbConnection`, so no second EF provider is required:

```csharp
await using var connection = new DotRocksFlightSqlDbConnection(options);
var builder = new DbContextOptionsBuilder<AnalyticsContext>();
builder.UseStarRocks(connection);

await using var context = new AnalyticsContext(builder.Options);
List<Event> events = await context.Events
    .Where(item => item.TenantId == tenantId)
    .ToListAsync(cancellationToken);
```

Async LINQ queries use Flight through the supplied connection. `SaveChangesAsync` uses the standard
Flight SQL update path when the server implements it, or the MySQL protocol when `WriteCommands`
fallback is explicitly configured. Keep `DotRocks.EntityFrameworkCore` referenced by the
application. Synchronous EF execution is rejected, and metadata/migration workflows that require
synchronous ADO.NET should continue to use `DotRocks.Data`.

## Explicit MySQL-protocol fallback

Fallback is disabled by default and requires both a MySQL-protocol connection string and an
explicit mode:

```csharp
await using var connection = new DotRocksFlightSqlDbConnection(
    options,
    "Server=127.0.0.1;Port=9030;User ID=root;Password=...",
    DotRocksFlightSqlFallbackMode.ReadQueries |
        DotRocksFlightSqlFallbackMode.WriteCommands);
```

- `ReadQueries` retries only when Flight query discovery fails with `Unavailable`,
  `Unimplemented`, or an HTTP transport failure. SQL errors, timeouts, cancellation, and result
  streaming failures are not replayed.
- `WriteCommands` routes writes to `DotRocks.Data` before contacting Flight. A write is never
  retried after an ambiguous Flight failure.
- Fallback never participates in a Flight transaction and does not attempt to copy session state.

These constraints avoid duplicate writes and cross-transport transaction illusions.

## Endpoint trust and validation

The exact FE scheme, host, and port are trusted automatically. Any different BE/CN address must be
listed in `AllowedEndpointAddresses` before DotRocks opens a channel and forwards authorization.
Matching uses the normalized scheme, host, and port; wildcards and host-only entries are not
supported.

> **StarRocks 3.5 note.** The 3.5 all-in-one deployment registers its BE with the FE as
> `127.0.0.1`, so query endpoints are advertised at loopback (for example
> `grpc://127.0.0.1:9419`). If you see *"The Flight SQL server returned an endpoint address
> that is not trusted"*, add the advertised BE address to `AllowedEndpointAddresses` —
> for a local or port-forwarded single-node setup that is typically
> `grpc://127.0.0.1:9419`. StarRocks 4.0.7 reuses the FE connection for query results, so
> no extra entry is needed there.

The package has in-process protocol tests for reads, standard update `DoPut`, authorization,
parameter binding, transactions, ADO.NET materialization, and EF Core queries. The live CI matrix
enables FE and BE Flight endpoints and runs Flight reads, EF Core queries, and explicitly routed
MySQL writes against StarRocks 3.5.5 and 4.0.7. Comparative server-backed benchmarks cover MySQL
rows, Flight rows, and direct Arrow batches.
