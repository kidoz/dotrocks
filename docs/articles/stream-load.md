# Stream Load

StarRocks Stream Load ingests data over HTTP into a table. DotRocks exposes it through
`DotRocksStreamLoadClient` for single-request loads (CSV and JSON) and through
`DotRocksStreamLoadTransaction` for two-phase transactional loads.

> The authoritative API surface lives in
> [`src/DotRocks.Data/Loading/`](https://github.com/kidoz/dotrocks/blob/main/src/DotRocks.Data/Loading/).
> This article mirrors the public type shapes. When the two disagree, the source wins.

## The client

`DotRocksStreamLoadClient` is `IDisposable` and takes a DotRocks connection string. It
separates the SQL query transport (port 9030) from the Stream Load HTTP transport (the FE
web port, default 8030), configured via `Stream Load Endpoint`:

```csharp
using DotRocks.Data.Loading;

using var client = new DotRocksStreamLoadClient(
    "Server=starrocks.example.com;Port=9030;User ID=loader;Password=secret;"
    + "Stream Load Endpoint=https://starrocks.example.com:8030"
);
```

HTTPS is enforced by default. See [Security](security.md#stream-load-transport-security)
for the credential-forwarding rules.

## Loading CSV

```csharp
await using Stream csv = File.OpenRead("events.csv");
DotRocksStreamLoadResult result = await client.LoadCsvAsync(
    "warehouse",
    "events",
    csv,
    new DotRocksStreamLoadOptions
    {
        Label = "events_20260626", // idempotency label
        Columns = "id,name,created_at",
        RowDelimiter = "\\n",
    }
);
```

A non-success status throws `DotRocksStreamLoadException`, so a returned `result` always
has `IsSuccess == true` — see [Interpreting the result](#interpreting-the-result).

The payload stream is uploaded with **no in-memory buffering** — it is streamed directly
to the server. If the server redirects, DotRocks replays the stream, so a **non-seekable**
stream is rejected on redirect (use a seekable stream, or point `Stream Load Endpoint`
directly at the final BE).

## Loading JSON

```csharp
await using Stream json = File.OpenRead("events.json");
DotRocksStreamLoadResult result = await client.LoadJsonAsync(
    "warehouse",
    "events",
    json,
    new DotRocksStreamLoadOptions { StripOuterArray = true, JsonPaths = "[\"$.id\",\"$.name\"]" }
);
```

## Gzip compression

CSV payloads can be gzip-compressed on the fly to reduce upload bandwidth. DotRocks
streams the payload through a `GZipStream` (never buffering it whole), so the request uses
chunked transfer encoding. Gzip is **CSV-only** — setting `Compression = Gzip` for a JSON
load throws `NotSupportedException`.

```csharp
new DotRocksStreamLoadOptions { Compression = DotRocksStreamLoadCompression.Gzip }
```

## Load options

`DotRocksStreamLoadOptions` is a mutable POCO; all properties are optional.

| Property | Type | Default | Notes |
|---|---|---|---|
| `Label` | `string?` | auto `dotrocks_<guid>` | Idempotency label — StarRocks rejects a duplicate, so a resend cannot double-load. |
| `Columns` | `string?` | null | Column mapping expression. |
| `Where` | `string?` | null | Row filter expression. |
| `ColumnSeparator` | `string?` | null | CSV separator (escaped, e.g. `\t`, `\x01`). |
| `RowDelimiter` | `string?` | null | CSV row delimiter (escaped, e.g. `\n`). |
| `StrictMode` | `bool?` | null | StarRocks strict mode. |
| `MaxFilterRatio` | `double?` | null | 0.0–1.0; `NaN` rejected. |
| `Timeout` | `TimeSpan?` | null | Must be > `Zero`. |
| `StripOuterArray` | `bool?` | null | JSON only. |
| `JsonPaths` | `string?` | null | JSON only. |
| `Partitions` | `IReadOnlyList<string>?` | null | Each non-empty, no `,`. |
| `Compression` | `DotRocksStreamLoadCompression` | `None` | `Gzip` is CSV-only. |

Header values are validated to contain no CR/LF (header injection prevention), and
partition names must not be empty or contain `,`.

## Interpreting the result

`DotRocksStreamLoadResult` carries the parsed StarRocks response:

| Property | Type | Notes |
|---|---|---|
| `Status` | `string` | StarRocks status string. |
| `Message` | `string?` | Server message. |
| `Label` | `string?` | Load label. |
| `NumberTotalRows` | `long` | Rows observed. |
| `NumberLoadedRows` | `long` | Rows loaded. |
| `NumberFilteredRows` | `long` | Rows filtered/rejected. |
| `NumberUnselectedRows` | `long` | Rows excluded. |
| `LoadBytes` | `long` | Bytes processed. |
| `LoadTimeMilliseconds` | `long` | Server-reported load duration. |
| `ErrorUrl` | `Uri?` | Absolute URL for rejected-row details. |
| `TransactionId` | `long?` | StarRocks transaction id, when present. |
| `Sequence` | `int?` | Transaction load sequence. |
| `IsSuccess` | `bool` | `Success`, `OK`, or publish-timeout. |
| `IsPublishTimeout` | `bool` | Data was written but visibility publish timed out (rows become queryable slightly later). |

A non-success server status never reaches the caller as a result: `LoadCsvAsync` and
`LoadJsonAsync` throw a `DotRocksStreamLoadException` carrying the status, the HTTP
status code, the parsed result, and the raw response body (`ResponseBody`). A returned
result always has `IsSuccess == true`; the one outcome worth checking explicitly is
`IsPublishTimeout` — the data was written, visibility just lags.

## Transactional Stream Load (two-phase)

For exactly-once ingestion across multiple loads, use a Stream Load transaction:
`begin → load(s) → prepare → commit`. The transaction is a state machine
(`Active → Prepared → Committed`, with `RolledBack` / `Failed` / `CompletionInDoubt`
terminals); operations on the wrong state throw `InvalidOperationException`.

```csharp
using var client = new DotRocksStreamLoadClient(connectionString);
DotRocksStreamLoadTransaction txn = await client.BeginTransactionAsync(
    "warehouse",
    "events",
    new DotRocksStreamLoadTransactionOptions { Label = "events_txn_001" }
);

try
{
    await using Stream batch1 = File.OpenRead("events-1.csv");
    await txn.LoadCsvAsync(batch1);

    await using Stream batch2 = File.OpenRead("events-2.csv");
    await txn.LoadCsvAsync(batch2);

    await txn.PrepareAsync();
    await txn.CommitAsync();
}
catch
{
    await txn.RollbackAsync();
    throw;
}
```

The transaction is not disposable, and nothing rolls it back implicitly. Call
`RollbackAsync()` on failure (as above) so the label's loads are discarded immediately
instead of lingering until the server's idle timeout expires.

### Transaction options

`DotRocksStreamLoadTransactionOptions` — `Label` is **required**:

| Property | Type | Default | Notes |
|---|---|---|---|
| `Label` | `string?` | — | **Required.** Throws if blank. |
| `IsMultiTable` | `bool` | `false` | Loads multiple tables. Requires StarRocks 4.0+; rejected on earlier versions. |
| `Timeout` | `TimeSpan?` | null | PREPARE → PREPARED. |
| `IdleTimeout` | `TimeSpan?` | null | Idle-rollback timeout. |
| `PreparedTimeout` | `TimeSpan?` | null | PREPARED → COMMITTED. |

### Multi-table transactions

Set `IsMultiTable = true` to load into more than one table within one transaction. This is
a StarRocks 4.0+ capability — DotRocks probes the server version once (lazily) and rejects
the begin on earlier lines. Each `LoadCsvAsync`/`LoadJsonAsync` overload takes an explicit
table name for multi-table use.

### In-doubt transactions

If the commit request is dispatched but the transport fails (or the server returns a
non-success), the transaction's outcome is **unknown**: the data may or may not be
committed. DotRocks throws `DotRocksStreamLoadTransactionInDoubtException` in that case,
carrying the label so you can reconcile with the server. This is deliberately not
silently retried.

## See also

- [Security](security.md) — Stream Load transport security and SSRF defense
- [Connection strings](connection-strings.md) — `Stream Load Endpoint` and related keywords
- [Observability](observability.md) — Stream Load metrics
- [`StreamLoad` sample](https://github.com/kidoz/dotrocks/blob/main/samples/DotRocks.Samples.StreamLoad/Program.cs)
- [`StreamLoadTransaction` sample](https://github.com/kidoz/dotrocks/blob/main/samples/DotRocks.Samples.StreamLoadTransaction/Program.cs)
