# Reading large results

StarRocks is an analytical engine, so queries that return millions of rows are normal. This
article covers how DotRocks streams them, what bounds memory and time, which transport to use,
and the things DotRocks deliberately does **not** offer — with the StarRocks behavior behind
each decision.

> The authoritative behavior lives in
> [`DotRocksDataReader`](https://github.com/kidoz/dotrocks/blob/main/src/DotRocks.Data/DotRocksDataReader.cs)
> and the protocol result types. This article mirrors them; when the two disagree, the source
> wins.

## Rows stream; memory is bounded per row

`ExecuteReaderAsync` returns as soon as the column metadata arrives. Each `ReadAsync` then
reads exactly one row packet off the wire and decodes it, so memory use is bounded by the
**widest single row**, not by the size of the result set:

```csharp
await using var command = connection.CreateCommand();
command.CommandText = "SELECT event_time, event_name FROM events WHERE tenant_id = @tenant";
command.Parameters.Add(new DotRocksParameter { ParameterName = "tenant", Value = tenantId });

await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
{
    // The previous row is released here; nothing accumulates.
    Process(reader.GetDateTime(0), reader.GetString(1));
}
```

This is enforced, not just intended: a live test scans 100,000 rows and asserts both that
opening the reader allocates almost nothing and that the whole drain stays under a per-row
allocation ceiling while retaining nothing afterwards.

**The guarantee is per row, not per field.** A single very large value is materialized whole.
`GetStream` and `GetTextReader` exist for API compatibility but read from the already-decoded
value, and `CommandBehavior.SequentialAccess` is accepted without imposing restrictions or
saving memory. A value larger than the reader's maximum logical packet size fails with an
error naming that limit.

## Time is bounded per fetch, not per query

`CommandTimeout` applies to **each row fetch**, re-armed as the reader advances. A stalled
server fails the read; a legitimately long scan that keeps producing rows is not cut off by
one total budget. `CommandTimeout = 0` disables it entirely, as elsewhere in ADO.NET.

`DbCommand.Cancel()` works while rows are being read, and the token passed to `ReadAsync`
cancels an in-flight fetch. A timed-out or cancelled read leaves the connection mid-stream, so
it is retired rather than returned to the pool.

Abandoning a reader early (leaving the `await using` block before the last row) drains the
remaining rows so the connection stays poolable, but that drain is bounded by the same command
timeout — it will retire the connection rather than block for the rest of a huge result set.

## Choosing a transport

| | MySQL protocol (`DotRocks.Data`) | Arrow Flight SQL (`DotRocks.FlightSql`) |
|---|---|---|
| StarRocks requirement | Any supported version | 3.5.1+, with `arrow_flight_port` set on **both** FE and BE |
| Wire format | Row-oriented text (or binary when prepared) | Columnar Arrow record batches |
| Surface | Full ADO.NET, EF Core, Dapper | Async ADO.NET, raw `RecordBatch`, EF Core via its `DbConnection` |
| Best for | General queries, writes, anything portable | Wide analytical scans where decode cost dominates |

The MySQL protocol is the default and works everywhere. Arrow Flight SQL avoids per-value text
parsing and is the faster path for large analytical reads, at the cost of a server-side
configuration requirement and an experimental client surface — see
[Arrow Flight SQL](arrow-flight-sql.md).

Note that StarRocks' own guidance is that client-side parsing usually costs more than reading
the bytes. If you switch transports for throughput, measure with your own schema: a result set
dominated by strings or `DECIMAL` benefits far less than one dominated by numerics, because
those values decode through the same text path on either transport.

## What DotRocks deliberately does not offer

These are not gaps waiting to be filled; StarRocks does not provide the underlying capability,
and offering an API that implied otherwise would be misleading.

- **No fetch size / server-side cursor.** StarRocks does not implement the MySQL
  `COM_STMT_FETCH` command, so there is no server-side cursor to page through. Results are
  pushed as a continuous row stream, backpressured only by TCP. A `FetchSize` property would
  have nothing to bind to.
- **No parallel endpoint fan-out for Flight SQL.** StarRocks returns exactly one endpoint per
  query, so there is nothing to parallelize across. The Flight win is columnar transport, not
  distributed reads.
- **`net_buffer_length` and related MySQL tuning knobs do nothing.** StarRocks documents them
  as accepted for client compatibility only.

To bound a result set, use `LIMIT` (with keyset pagination if you need to resume), or set the
server-side `sql_select_limit` session variable as a safety valve against an accidental full
scan.

## Extracting very large data sets

When the goal is to move a large table somewhere else rather than process rows in .NET, the
server-side unload path is usually better than reading through a client:
`INSERT INTO FILES(...)` writes Parquet or CSV directly to object storage, and StarRocks has
recommended it since 3.2. DotRocks can issue that statement like any other, but reading the
resulting files is the caller's job — this is an extraction pipeline, not a `DbDataReader`
acceleration.

## Performance notes

- Each row costs roughly 425 bytes of allocation for a narrow three-column row: the decoded
  value array, one box per primitive, and copies of strings and byte arrays. The row's wire
  buffer is pooled and recycled, so it does not add to that.
- Prefer the typed accessors (`GetInt64`, `GetString`, …) over `GetValue` and the indexers,
  which return `object`.
- Reading by column name is a dictionary lookup built once per result set, so it is no longer
  proportional to column count — but reading by ordinal still avoids the lookup entirely.
- The `MaterializeRows` benchmark guards this path's per-row allocation in CI; see the
  [build and test section](https://github.com/kidoz/dotrocks#build-and-test) of the README for
  how the performance budgets are enforced.

## See also

- [Arrow Flight SQL](arrow-flight-sql.md) — the columnar transport and its security model
- [Stream Load](stream-load.md) — the ingestion counterpart to this article
- [Observability](observability.md) — the metrics and spans emitted while reading
