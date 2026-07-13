# DotRocks

[![Language](https://img.shields.io/badge/language-C%23-512BD4)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET SDK](https://img.shields.io/badge/.NET%20SDK-10.0.301-512BD4)](https://github.com/kidoz/dotrocks/blob/main/global.json)
[![License](https://img.shields.io/badge/license-MIT-blue)](https://github.com/kidoz/dotrocks/blob/main/LICENSE)

**DotRocks for StarRocks** — a native .NET driver, Entity Framework Core provider, and
Roslyn analyzer suite built specifically for [StarRocks](https://www.starrocks.io/).

> DotRocks is an independent open-source project. It is not an official StarRocks
> project and does not imply StarRocks endorsement.

## Packages

| Package | Description |
|---|---|
| `DotRocks.Data` | Native ADO.NET provider with its own managed StarRocks protocol implementation. |
| `DotRocks.EntityFrameworkCore` | EF Core relational provider built on `DotRocks.Data`. |
| `DotRocks.EntityFrameworkCore.Design` | Design-time EF Core services for migrations. |
| `DotRocks.Analyzers` | Roslyn analyzers for correct, secure DotRocks usage. |
| `DotRocks.Analyzers.CodeFixes` | Optional IDE code fixes for mechanical DotRocks analyzer diagnostics. |

## Status

The latest tagged release is DotRocks 1.3.2. The `main` branch is post-1.3.2; this
README tracks `main`, and unreleased changes are listed in
[CHANGELOG.md](https://github.com/kidoz/dotrocks/blob/main/CHANGELOG.md).
The ADO.NET driver (`DotRocks.Data`), EF Core provider (`DotRocks.EntityFrameworkCore`),
and analyzer suite (`DotRocks.Analyzers`) are validated against live StarRocks 3.5.5 and
4.0.7 in CI. Supported features are implemented and tested; unsupported behavior fails
explicitly.

DotRocks implements **its own** managed StarRocks client protocol. It takes no runtime
dependency on MySqlConnector, Oracle MySQL Connector/NET, Pomelo, or any other MySQL
driver, and it is not a general-purpose MySQL driver.

## Requirements

- .NET 10 SDK (pinned via `global.json`)
- C# 14
- StarRocks FE reachable on the MySQL protocol port (default 9030); Stream Load also
  needs the FE HTTP endpoint (default 8030)

## Documentation

- [Getting started](https://github.com/kidoz/dotrocks/blob/main/docs/articles/getting-started.md)
- [Connection strings](https://github.com/kidoz/dotrocks/blob/main/docs/articles/connection-strings.md)
- [Security](https://github.com/kidoz/dotrocks/blob/main/docs/articles/security.md)
- [Stream Load](https://github.com/kidoz/dotrocks/blob/main/docs/articles/stream-load.md)
- [Observability](https://github.com/kidoz/dotrocks/blob/main/docs/articles/observability.md)
- [EF Core entity mapping](https://github.com/kidoz/dotrocks/blob/main/docs/articles/ef-core-entity-mapping.md)
- [Analyzers](https://github.com/kidoz/dotrocks/blob/main/docs/articles/analyzers.md)
- [StarRocks 3.x driver developer notes](https://github.com/kidoz/dotrocks/blob/main/docs/starrocks-3x-driver-developer-notes.md)
- [StarRocks 4.x driver developer notes](https://github.com/kidoz/dotrocks/blob/main/docs/starrocks-4x-driver-developer-notes.md)

## ADO.NET usage

Create connections directly when you need a short-lived provider object:

```csharp
await using var connection = new DotRocksConnection(
    "Server=127.0.0.1;Port=9030;User ID=root"
);
await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = "SELECT 1";
object? value = await command.ExecuteScalarAsync();
```

`DotRocksCommand.ParameterMode` selects the parameter binding and execution path:

- `Auto` (the default) and `TextProtocol` validate named `@` placeholders and parameter
  metadata before executing StarRocks text SQL with formatted parameter values.
  `Prepare()` / `PrepareAsync()` validate the command shape for these modes.
- `ServerPrepared` uses the StarRocks server-side prepared (binary) protocol —
  `COM_STMT_PREPARE` / `COM_STMT_EXECUTE` / `COM_STMT_CLOSE`, verified against StarRocks 4.0.7. Use
  positional `?` placeholders and add parameters in order; values are sent with the binary
  parameter encoding and results are decoded from binary rows. Unsupported binary parameter types
  fail with a `DotRocksUnsupportedFeatureException` (which derives from
  `DotRocksException`). Prepared statements are cached and reused per physical connection, so
  re-executing the same SQL avoids re-preparing. StarRocks 4.0.7 allows only `SELECT` in the
  prepared protocol — a prepared `INSERT` / `UPDATE` / `DELETE` is rejected by the server, so use
  `Auto` (text protocol) for parameterized writes.

```csharp
await using var command = (DotRocksCommand)connection.CreateCommand();
command.CommandText = "SELECT event_name FROM events WHERE tenant_id = ? AND active = ?";
command.ParameterMode = DotRocksParameterMode.ServerPrepared;
command.Parameters.Add(new DotRocksParameter { Value = tenantId });
command.Parameters.Add(new DotRocksParameter { Value = true });
await using var reader = await command.ExecuteReaderAsync(cancellationToken);
```

Use `DotRocksDataSource` for shared configuration and DotRocks pooling:

```csharp
await using var dataSource = new DotRocksDataSource(
    "Server=127.0.0.1;Port=9030;User ID=root;Pooling=true"
);

await using DbConnection connection = await dataSource.OpenConnectionAsync();
```

Provider-agnostic ADO.NET code can use `DotRocksFactory`:

```csharp
DbProviderFactory factory = DotRocksFactory.Instance;

using DbConnectionStringBuilder builder = factory.CreateConnectionStringBuilder()!;
builder.ConnectionString = "Server=127.0.0.1;Port=9030;User ID=root";

await using DbDataSource dataSource = factory.CreateDataSource(builder.ConnectionString);
await using DbConnection connection = await dataSource.OpenConnectionAsync();
```

For SQL protocol TLS, `Ssl Mode` defaults to `Preferred`: the connection upgrades to TLS
when the server advertises support and falls back to plaintext otherwise. Set `Required` to
fail the connection when TLS cannot be negotiated (the only mode that resists an active
downgrade attacker), or `Disabled` to never request TLS.
DotRocks uses platform certificate validation (chain and host name) by default and checks
revocation in offline mode to avoid a blocking OCSP/CRL fetch during the handshake;
`Trust Server Certificate=true` disables validation and is intended only for controlled
local test environments with self-signed certificates; it requires `Ssl Mode=Required` so it
is never silently ignored on a plaintext fallback.

DotRocks gates version-specific behavior on the StarRocks version, which it reads with
`SELECT current_version()` (the MySQL-protocol handshake only reports a compatibility version
such as `8.0.33`). Set `Server Compatibility Level` to pin that version — for example
`Server Compatibility Level=4.0` — when detection is unavailable or you want to force a
specific capability set. When unset, DotRocks detects the version per connection.

## Entity Framework Core

`DotRocks.EntityFrameworkCore` provides the EF Core provider surface for StarRocks:
`UseStarRocks`, EF-managed relational connections, raw SQL commands,
`FromSqlRaw`, and the LINQ query subset verified against StarRocks.
Pin the target server with `starRocks => starRocks.ServerVersion(new StarRocksServerVersion(4, 0, 7))`;
building the options never contacts the server. To discover the version once at startup, call
`await StarRocksServerVersion.DetectAsync(connectionString)` and cache the result.

```csharp
using DotRocks.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;

DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
    .UseStarRocks(
        "Server=127.0.0.1;Port=9030;User ID=root",
        starRocks => starRocks.ServerVersion(new StarRocksServerVersion(4, 0, 7)))
    .Options;

await using var context = new AppDbContext(options);

int count = await context.Widgets
    .Where(widget => widget.IsActive && widget.Name.StartsWith("prod"))
    .CountAsync();
```

Supported EF Core query surface:

- `Database.ExecuteSqlRawAsync` for raw SQL commands.
- `DbSet<TEntity>.FromSqlRaw("SELECT ...")` materialization.
- LINQ `Where`, comparison operators, boolean `&&` / `||`, nullable comparisons.
- Captured scalar LINQ parameters render as `@...` placeholders with values carried in
  `DbParameter`s, including strings, decimals, nullable values, bools, and
  `DotRocksDecimal`.
- `OrderBy`, `ThenBy`, `OrderByDescending`, `Skip`, `Take`, `Distinct`.
- `Contains` over constant/parameter collections for `IN (...)`.
- `StartsWith`, `EndsWith`, and `Contains` for strings using StarRocks `LIKE` (wildcards are
  backslash-escaped; StarRocks does not accept an `ESCAPE` clause and is not emitted one).
- `FirstOrDefaultAsync`, `SingleAsync`, `ToListAsync`, `CountAsync`, `AnyAsync`.
- Aggregate basics: `Min`, `Max`, `Sum`, `Average`.
- Explicit relational joins via `Join` and `GroupJoin`/`SelectMany`+`DefaultIfEmpty`
  (`INNER JOIN` / `LEFT JOIN`) and cross joins, translated to StarRocks SQL.
- `GroupBy` with key projection, `HAVING` predicates, and the aggregate functions above.
- Projection into anonymous objects and simple DTOs.
- `SaveChangesAsync` for a constrained single-table write model: explicit primary key,
  scalar properties only, single-column keys, `ValueGeneratedNever()`, no navigations, no
  generated values, and no concurrency tokens. Supported DML is parameterized `INSERT`,
  `UPDATE ... WHERE pk = @p`, and `DELETE ... WHERE pk = @p`. Save **one row per
  `SaveChanges`**: StarRocks rejects a second DML against a table already written in the same
  transaction (error 5303), so a multi-row `SaveChanges` to one table fails — use one row per
  call, or Stream Load for bulk. `SaveChanges` inside a user transaction works; StarRocks has
  no `SAVEPOINT`, so EF savepoints are disabled. DotRocks does not model OLTP-style
  affected-row concurrency checks.
- Minimal migrations can create StarRocks databases from `EnsureSchema` as
  `CREATE DATABASE IF NOT EXISTS`, create and drop StarRocks tables, and create the EF
  migrations history table. `CREATE TABLE` defaults to `DUPLICATE KEY`, hash
  distribution by the key columns, one bucket, and `replication_num = 1`. Configure
  table shape with `HasStarRocksDuplicateKey(...)`, `HasStarRocksPrimaryKey(...)`,
  `HasStarRocksUniqueKey(...)`, `HasStarRocksHashDistribution(...)`,
  `DistributedRandomly(...)` (emits `DISTRIBUTED BY RANDOM`), `HasSortKey(...)` (emits
  `ORDER BY`), `HasStarRocksProperty(name, value)` (adds a `PROPERTIES` entry), and
  `HasStarRocksReplicationNum(...)`. The
  design-time package is `DotRocks.EntityFrameworkCore.Design`. Down migrations are
  limited to the same conservative DDL boundary; table rollback through `DropTable` is
  supported, while `DROP DATABASE` and schema mutations remain explicit failures.

Unsupported EF Core behavior is explicit:

- `ExecuteUpdate`, and `ExecuteDelete`.
- `EnsureCreated` and schema deletion.
- migration schema mutations beyond conservative database creation and table
  creation/drop, including `DROP DATABASE`, add/drop/alter/rename column, rename table,
  indexes, add/drop primary key, foreign keys, defaults, and computed columns.
- idempotent migration scripts.
- composite-key writes, and multi-row `SaveChanges` to a single table (StarRocks error 5303).
- `SAVEPOINT` (unsupported by StarRocks; EF savepoints are disabled).
- `Include`, navigation materialization, and navigation-based joins: relationships are
  rejected at model validation, so joins must be expressed explicitly across `DbSet`s.
- binary/varbinary EF mapping until byte-array query translation and materialization are
  broader than the verified ADO.NET reader path.

These constraints are enforced at **model validation**, when the model is first built.
DotRocks requires the whole mapped model to be write-safe: any keyed entity with a
navigation, complex property, composite key, concurrency token, generated/default/computed
value, or binary property is rejected up front, even for a query-only `DbContext`.
Configure mapped properties accordingly, for example `ValueGeneratedNever()` on keys.

A compilable EF Core sample lives at
[`samples/DotRocks.Samples.EntityFrameworkCore`](https://github.com/kidoz/dotrocks/tree/main/samples/DotRocks.Samples.EntityFrameworkCore).
It demonstrates `UseStarRocks`, `ServerVersion(...)`, `ValueGeneratedNever()`, `SaveChangesAsync`
insert/update/delete, and a minimal hand-authored migration.

Other compilable samples cover the rest of the surface:

- [`DotRocks.Samples.AdoNet`](https://github.com/kidoz/dotrocks/tree/main/samples/DotRocks.Samples.AdoNet) —
  `DotRocksDataSource`, a parameterized query, and streaming a reader.
- [`DotRocks.Samples.DependencyInjection`](https://github.com/kidoz/dotrocks/tree/main/samples/DotRocks.Samples.DependencyInjection) —
  registering the dependency-free `DotRocksDataSource` with `Microsoft.Extensions.DependencyInjection`.
- [`DotRocks.Samples.Dapper`](https://github.com/kidoz/dotrocks/tree/main/samples/DotRocks.Samples.Dapper) —
  Dapper over a `DotRocksConnection`.
- [`DotRocks.Samples.SecureConnection`](https://github.com/kidoz/dotrocks/tree/main/samples/DotRocks.Samples.SecureConnection) —
  requiring TLS with `Ssl Mode=Required` and full certificate/host-name validation.
- [`DotRocks.Samples.ConnectionPooling`](https://github.com/kidoz/dotrocks/tree/main/samples/DotRocks.Samples.ConnectionPooling) —
  pool sizing and lifetime, connection reuse, and transient-open retries.
- [`DotRocks.Samples.Transactions`](https://github.com/kidoz/dotrocks/tree/main/samples/DotRocks.Samples.Transactions) —
  `DotRocksTransaction` begin/commit/rollback over a `DotRocksConnection`.
- [`DotRocks.Samples.StreamLoad`](https://github.com/kidoz/dotrocks/tree/main/samples/DotRocks.Samples.StreamLoad) —
  streaming CSV Stream Load without buffering the payload in memory.
- [`DotRocks.Samples.StreamLoadTransaction`](https://github.com/kidoz/dotrocks/tree/main/samples/DotRocks.Samples.StreamLoadTransaction) —
  a two-phase Stream Load transaction (begin → load → prepare → commit) for all-or-nothing ingestion.

StarRocks transaction behavior is characterized by live tests. `COMMIT WORK` makes EF
`SaveChangesAsync` rows visible. Some StarRocks builds accept `ROLLBACK WORK` for DML
but still expose inserted rows; DotRocks keeps rollback tests as characterization tests
and does not claim OLTP rollback semantics beyond the behavior verified by the target
StarRocks server.

EF Core type mapping:

| StarRocks type | EF CLR type |
| --- | --- |
| `BOOLEAN` | `bool` |
| `TINYINT` | `sbyte` |
| `SMALLINT` | `short` |
| `INT`, `INTEGER`, `MEDIUMINT` | `int` |
| `BIGINT` | `long` |
| `LARGEINT` | `Int128` |
| `FLOAT` | `float` |
| `DOUBLE` | `double` |
| `DECIMAL(p,s)` where `p <= 29` | `decimal` |
| `DECIMAL(p,s)` where `p >= 30` | `DotRocksDecimal` |
| `DATE` | `DateOnly` |
| `DATETIME` | `DateTime` |
| `TIME` | `TimeOnly` when the value is returned as a time string/span |
| `CHAR(36)` | `Guid` |
| `CHAR`, `VARCHAR`, `STRING`, `TEXT` | `string` |
| `JSON` | `string` |

EF Core maps only the `json` store type, to `string`. `ARRAY`, `MAP`, and `STRUCT` are not EF-mapped,
and `DotRocksJson` (below) is an ADO.NET reader feature, not an EF type mapping.

**Reading JSON and complex types over ADO.NET.** The `DotRocksDataReader` returns `JSON`, `ARRAY`,
`MAP`, and `STRUCT` values as a raw `string` by default. For the cases exercised by the integration
suite, StarRocks 4.0.7 sends `JSON` typed as `STRING` and `ARRAY` / `MAP` / `STRUCT` typed as
`VAR_STRING`, each serialized as JSON-formatted text (for example `[1,2,3]`, `{"k1":1}`,
`{"x":1,"y":"two"}`, including nested values, `null` elements, escaped strings, and decimal/date
values). None are distinguishable from a plain string by wire type. For lossless, opt-in typed
access call `reader.GetFieldValue<DotRocksJson>(ordinal)`: `DotRocksJson` preserves the server's
exact bytes and offers `Parse()` for a caller-owned `System.Text.Json.JsonDocument`. The
aggregate-state types `BITMAP`, `HLL`, and `PERCENTILE` are opaque — selecting them directly yields
`NULL` over the text protocol — so read them through their StarRocks accessor functions (for example
`bitmap_to_string(...)`, `hll_cardinality(...)`).

Projecting high-precision StarRocks decimals to `decimal` can throw
`DotRocksPrecisionLossException`; use `DotRocksDecimal` for lossless values.
ADO.NET verifies `VARBINARY` / `BLOB` result values as `byte[]`, including
`GetBytes(...)`; EF Core byte-array mapping remains unsupported until the EF query
surface is verified end to end.

## Analyzers

`DotRocks.Analyzers` ships Roslyn diagnostics for source-visible DotRocks security and
provider-compatibility issues. `DotRocks.Analyzers.CodeFixes` ships optional IDE code
fixes for diagnostics where the correction is mechanical. Reference these as
development-time analyzer packages; they do not add runtime assemblies to application
output.

Package consumption:

```xml
<PackageReference Include="DotRocks.Data" Version="1.3.2" />
<PackageReference Include="DotRocks.EntityFrameworkCore" Version="1.3.2" />
<PackageReference Include="DotRocks.EntityFrameworkCore.Design" Version="1.3.2" PrivateAssets="all" />
<PackageReference Include="DotRocks.Analyzers" Version="1.3.2" PrivateAssets="all" />
<PackageReference Include="DotRocks.Analyzers.CodeFixes" Version="1.3.2" PrivateAssets="all" />
```

The test suite validates these packages through a local NuGet-source consumer project
and verifies analyzer diagnostics fire from package consumption, not only project
references.

Current diagnostics:

| ID | Severity | Detects | Fix |
| --- | --- | --- | --- |
| `DTR0001` | Warning | HTTP Stream Load endpoint used with credentials in a visible DotRocks connection string or `DotRocksConnectionStringBuilder` initializer. | Use an HTTPS Stream Load endpoint when credentials are present. A code fix updates simple source-visible literals from `http://` to `https://`. |
| `DTR0002` | Warning | EF Core key properties without a visible `ValueGeneratedNever()` configuration. | Configure each writable key property with `ValueGeneratedNever()`. A code fix adds the property configuration for simple `HasKey(entity => entity.Id)` chains. |
| `DTR0003` | Warning | EF Core `binary` / `varbinary` column type mappings. | Avoid EF binary mappings until DotRocks verifies EF read/write binary support. No automatic fix is provided. |
| `DTR0004` | Warning | Source-visible double completion of `DotRocksTransaction` or `DotRocksStreamLoadTransaction`. | Commit or roll back a transaction object once and do not reuse it after completion. No automatic fix is provided because transaction flow needs human intent. |
| `DTR0005` | Warning | EF Core `EnsureCreated` / `EnsureDeleted` calls. | Use migrations for conservative StarRocks DDL; these database creator APIs are unsupported. |
| `DTR0006` | Warning | EF Core `ExecuteUpdate` / `ExecuteDelete` calls. | Use tracked single-row `SaveChanges` or raw SQL with explicit parameters; bulk LINQ DML is not translated. |
| `DTR0007` | Warning | Source-visible `AddRange` / `UpdateRange` / `RemoveRange` followed by one `SaveChanges` call. | Save one row per `SaveChanges`, or use Stream Load for bulk ingestion. |
| `DTR0008` | Warning | EF Core entities configured with a composite primary key (`HasKey(e => new { ... })`) in `OnModelCreating`. | Use a single-column primary key for writable entities, or `HasNoKey()` for read-only entities. Escalate to a build error with `dotnet_diagnostic.DTR0008.severity = error`. No automatic fix is provided. |
| `DTR0009` | Warning | Interpolated or concatenated SQL assigned to `DotRocksCommand.CommandText` or passed to its constructor. | Use parameter placeholders (for example `@id`) with `DotRocksParameter` values. Escalate to a build error with `dotnet_diagnostic.DTR0009.severity = error`. No automatic fix is provided because parameterization needs human intent. |
| `DTR0010` | Warning | An async DotRocks call that accepts a `CancellationToken` but does not pass the one available in the enclosing method. | Pass the available `CancellationToken` to the async call. No automatic fix is provided. |
| `DTR0011` | Warning | Blocking on an async DotRocks call with `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`. | `await` the operation instead of blocking on it. No automatic fix is provided. |
| `DTR0012` | Warning | A hard-coded password embedded in a DotRocks connection string literal or local string. | Load credentials from configuration, environment, or a secret store instead of a string literal. No automatic fix is provided. |

Disposal of connections, commands, readers, and transactions is covered by the built-in
.NET analyzer `CA2000`; DotRocks does not duplicate that rule.

Analyzer limits: diagnostics inspect source-visible constants, local string assignments,
`DotRocksConnectionStringBuilder` initializers, and local method bodies. They do not perform
interprocedural data-flow analysis, inspect runtime-built connection strings, prove transaction
state across method boundaries, or prove whether a range change contains only one entity at
runtime.

## Stream Load

Use `DotRocksStreamLoadClient` for StarRocks HTTP Stream Load. The client uses the same
connection string credentials and reads payloads from the supplied stream.

```csharp
using DotRocks.Data.Loading;

using var client = new DotRocksStreamLoadClient(
    "Server=starrocks.example.com;Port=9030;User ID=loader;Password=secret;Stream Load Endpoint=https://starrocks.example.com:8030"
);

await using Stream csv = File.OpenRead("events.csv");
DotRocksStreamLoadResult result = await client.LoadCsvAsync(
    "warehouse",
    "events",
    csv,
    new DotRocksStreamLoadOptions
    {
        Label = "events_20260619",
        Columns = "id,name,created_at",
        ColumnSeparator = ",",
        RowDelimiter = "\\n",
        Partitions = ["p20260601"],
        Compression = DotRocksStreamLoadCompression.Gzip,
    }
);
```

Set `Partitions` to restrict a load to specific StarRocks partitions, and `Compression =
DotRocksStreamLoadCompression.Gzip` to gzip the payload on the fly. DotRocks streams the
compressed bytes (the upload is never buffered in memory) and reports the StarRocks `format` as
`gzip`, verified against StarRocks 4.0.7. Gzip is supported for CSV payloads only; using it with
JSON throws `NotSupportedException`.

Transactional Stream Load uses the StarRocks begin/load/prepare/commit HTTP flow. The
transaction object is single-use; after commit, rollback, or a failed remote call it rejects
further operations.

```csharp
using DotRocks.Data.Loading;

using var client = new DotRocksStreamLoadClient(
    "Server=starrocks.example.com;Port=9030;User ID=loader;Password=secret;Stream Load Endpoint=https://starrocks.example.com:8030"
);

DotRocksStreamLoadTransaction transaction = await client.BeginTransactionAsync(
    "warehouse",
    "events",
    new DotRocksStreamLoadTransactionOptions { Label = "events_tx_20260619" }
);

await using Stream csv = File.OpenRead("events.csv");
await transaction.LoadCsvAsync(
    csv,
    new DotRocksStreamLoadOptions
    {
        Columns = "id,name,created_at",
        ColumnSeparator = ",",
        RowDelimiter = "\\n",
    }
);

await transaction.PrepareAsync();
await transaction.CommitAsync();

// To abandon an active or prepared transaction before commit:
// await transaction.RollbackAsync();
```

HTTP Stream Load endpoints send Basic authentication without transport encryption and are
rejected by default. For trusted local test environments such as the pinned Docker
container, set `Allow Insecure Stream Load=true` explicitly.

The payload is streamed to StarRocks without buffering the whole input. One limitation:
StarRocks redirects the load from the FE to a BE/CN, and replaying the request after a redirect
requires rewinding the body. DotRocks rewinds **seekable** streams automatically, but a
**non-seekable** payload (a pipe, network, or compression stream) cannot be replayed and fails
explicitly after a redirect rather than being silently buffered. For non-seekable sources, use a
seekable stream (for example buffer to a `MemoryStream` or file) or send directly to a BE/CN
endpoint that does not redirect.

## Observability

DotRocks emits OpenTelemetry-compatible tracing and metrics. Subscribe by name via
`DotRocksTelemetry.ActivitySourceName` and `DotRocksTelemetry.MeterName` (both
`"DotRocks.Data"`):

- Activities: `dotrocks.connection.open`, `dotrocks.command.execute`.
- Metrics: `dotrocks.connections.opened`, `dotrocks.commands.executed`,
  the `dotrocks.command.duration`, `dotrocks.connection.open.duration`, and
  `dotrocks.transaction.duration` histograms (ms, tagged with a bounded `outcome`), the pool
  instruments (`dotrocks.pool.connections.idle` / `.active`, `dotrocks.pool.lease.wait_time`,
  `dotrocks.pool.connections.discarded`), and Stream Load instruments
  (`dotrocks.stream_load.duration` histogram (ms), `dotrocks.stream_load.rows_loaded`,
  `dotrocks.stream_load.rows_filtered`, `dotrocks.stream_load.bytes`), all tagged only with a
  bounded `outcome`.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(DotRocksTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(DotRocksTelemetry.MeterName));
```

The command metrics carry only bounded labels: `outcome` (`success`, `error`, `canceled`,
`timeout`) and `operation` (a low-cardinality operation name such as `SELECT`/`INSERT`). SQL text,
parameter values, connection strings, user names, host names, and database names are never used as
metric labels. DotRocks keeps native metric names such as `dotrocks.command.duration` in ms and
does not emit a parallel `db.client.operation.duration` metric.

Spans carry safe attributes aligned with the OpenTelemetry database semantic conventions:
`db.system.name` (`other_sql`), `db.operation.name` and `db.query.summary` (a low-cardinality
operation such as `SELECT`/`INSERT`, never literals), `server.port`, `db.namespace` when known, and
`error.type` / `db.response.status_code` on failure. **By default DotRocks never emits raw SQL
(`db.query.text`), parameter names or values, connection strings, passwords, usernames, server
message text, or tenant-bearing host names** — failures record a stable classification, not the
exception or server message. DotRocks runtime packages require no OpenTelemetry SDK or exporter
packages; subscribe with `System.Diagnostics` listeners or the OpenTelemetry SDK as shown.

Pooled connections are liveness-checked on lease, so a connection the server closed while
idle is discarded rather than handed out. Set `Connection Lifetime` (seconds; `0` = unbounded,
the default) to retire a physical connection once it reaches that age, with a small per-connection
jitter so connections opened together do not all expire at once.

DotRocks does not perform a verified per-lease session reset. A connection that ran a
session-mutating statement (`USE` or `SET`) is **discarded on return instead of reused** so
the current database and session variables cannot leak into the next lease. Pure query and
DML workloads pool and reuse connections normally.

Pool activity is observable through the `DotRocks.Data` `Meter`: `dotrocks.pool.connections.idle`
and `dotrocks.pool.connections.active` (gauges), `dotrocks.pool.lease.wait_time` (histogram), and
`dotrocks.pool.connections.discarded` (counter).

Connection pooling is **process-global**, keyed by the normalized connection configuration, and is
not owned by any single `DotRocksConnection` or `DotRocksDataSource`. Disposing a `DotRocksDataSource`
stops it from opening new connections but does **not** evict that configuration's idle physical
connections from the shared pools. To release all idle pooled connections process-wide, call
`DotRocksConnection.ClearAllPools()`; to release only one configuration's pool, call
`DotRocksConnection.ClearPool(connection)` or `dataSource.ClearPool()`.

Data-source-scoped pools are not provided. Pooling stays process-global so the default does
not change socket counts or `Maximum Pool Size` semantics, and `ClearPool()` gives
deterministic teardown.

## Build and test

Common tasks are exposed via [`just`](https://github.com/casey/just) (see `justfile`):

```bash
just            # list recipes
just ci         # restore, format check, build, test
just build
just test
just pack
```

The equivalent raw commands:

```bash
dotnet tool restore
dotnet restore --locked-mode
dotnet csharpier check .
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet pack --configuration Release --no-build
```

Run the server-free, budgeted BenchmarkDotNet suite when changing protocol hot paths:

```bash
just bench            # or: dotnet run --project benchmarks/DotRocks.Benchmarks -c Release -- --anyCategories Local
```

Benchmark results fail the process if a measured benchmark exceeds its configured mean
time or allocation budget, if a new budgeted benchmark is added without a budget entry, or if
no measurements were validated at all (for example a typoed filter or a Dry-only run).

Server-backed stress benchmarks (warm-pool open, lease latency and contention, cancellation
discard, large-result streaming, EF Core materialization, and Stream Load throughput) need a live
StarRocks server and are observational, so they are excluded from the numeric budget gate. A
benchmark execution or setup failure still fails the process:

```bash
just starrocks-up     # start StarRocks
just bench-server     # runs the ServerBacked category; override with DOTROCKS_BENCH_CONNECTION_STRING
```

Integration tests run against a real StarRocks server and are documented separately; the
commands above cover the server-free unit and protocol tests. CI keeps unit/build checks,
package validation, StarRocks live integration, and release publishing in separate
workflows so package artifacts and live-server gates are visible independently.

### Verified robustness

DotRocks has focused regression coverage for malformed protocol packets, including
length-encoded integer/string edge cases, truncated result metadata, invalid OK/ERR
packets, oversized result counts, oversized row value claims, and trailing text-row
bytes. Secret-hygiene tests cover connection-string redaction, connection/open failures,
Stream Load HTTP failures, Stream Load result failures, and debug-display surfaces.
Cancellation tests verify open cancellation, command timeout, user cancellation,
`DbCommand.Cancel()`, active-reader cancellation, and broken pooled connection discard.
SQL protocol TLS is covered by fake-server tests for SSL-request negotiation, certificate
validation failure handling, and successful TLS upgrade with an explicit local trust
override.

### StarRocks compatibility harness (Docker)

```bash
just starrocks-up      # start a pinned StarRocks container, wait until query-ready
just harness           # probe the handshake and write a sanitized report
just starrocks-down    # stop and clean up
```

The harness reads the initial handshake through the DotRocks framing and handshake layers
and writes a sanitized JSON report (authentication challenge bytes are never recorded).

## License

Licensed under the [MIT License](https://github.com/kidoz/dotrocks/blob/main/LICENSE).
