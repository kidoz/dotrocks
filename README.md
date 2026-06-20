# DotRocks

[![Language](https://img.shields.io/badge/language-C%23-512BD4)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET SDK](https://img.shields.io/badge/.NET%20SDK-10.0.301-512BD4)](global.json)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

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

Early development. Nothing here is described as working unless it is built and tested.

DotRocks implements **its own** managed StarRocks client protocol. It takes no runtime
dependency on MySqlConnector, Oracle MySQL Connector/NET, Pomelo, or any other MySQL
driver, and it is not a general-purpose MySQL driver.

## Requirements

- .NET 10 SDK (pinned via `global.json`)
- C# 14

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

`DotRocksCommand.Prepare()` / `PrepareAsync()` currently perform conservative
client-side preparation for text commands: named placeholders and parameter metadata are
validated up front, and execution still sends StarRocks text SQL with safely formatted
current parameter values. The driver does not use the MySQL binary prepared-statement
protocol yet.

Use `DotRocksDataSource` when one normalized configuration should create many logical
connections and participate in DotRocks pooling:

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

For SQL protocol TLS, set `Ssl Mode=Required`. `Ssl Mode` defaults to `Disabled`, which
sends credentials without transport encryption — set `Required` for any non-local server.
DotRocks uses platform certificate validation (chain and host name) by default and checks
revocation in offline mode to avoid a blocking OCSP/CRL fetch during the handshake;
`Trust Server Certificate=true` disables validation and is intended only for controlled
local test environments with self-signed certificates.

## Entity Framework Core

`DotRocks.EntityFrameworkCore` provides the current EF Core provider surface for
StarRocks: `UseStarRocks`, EF-managed relational connections, raw SQL commands,
`FromSqlRaw`, and a deliberately small LINQ query subset verified against StarRocks.

```csharp
using Microsoft.EntityFrameworkCore;

DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
    .UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root")
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
- `StartsWith`, `EndsWith`, and `Contains` for strings using StarRocks `LIKE`.
- `FirstOrDefaultAsync`, `SingleAsync`, `ToListAsync`, `CountAsync`, `AnyAsync`.
- Aggregate basics: `Min`, `Max`, `Sum`, `Average`.
- Projection into anonymous objects and simple DTOs.
- `SaveChangesAsync` for a constrained single-table write model: explicit primary key,
  scalar properties only, single-column keys, `ValueGeneratedNever()`, no navigations, no
  generated values, and no concurrency tokens. Supported DML is parameterized `INSERT`,
  `UPDATE ... WHERE pk = @p`, and `DELETE ... WHERE pk = @p`. Multiple changed
  entities are emitted as separate parameterized commands; DotRocks does not currently
  model OLTP-style affected-row concurrency checks.
- Minimal migrations can create StarRocks databases from `EnsureSchema` as
  `CREATE DATABASE IF NOT EXISTS`, create and drop StarRocks tables, and create the EF
  migrations history table. `CREATE TABLE` defaults to `DUPLICATE KEY`, hash
  distribution by the key columns, one bucket, and `replication_num = 1`. Configure
  table shape with `HasStarRocksDuplicateKey(...)`, `HasStarRocksPrimaryKey(...)`,
  `HasStarRocksHashDistribution(...)`, and `HasStarRocksReplicationNum(...)`. The
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
- composite-key writes.
- joins, `Include`, navigation materialization, and `GroupBy`.
- binary/varbinary EF mapping until byte-array query translation and materialization are
  broader than the verified ADO.NET reader path.

A compilable EF Core sample lives at
[`samples/DotRocks.Samples.EntityFrameworkCore`](samples/DotRocks.Samples.EntityFrameworkCore).
It demonstrates `UseStarRocks`, `ValueGeneratedNever()`, `SaveChangesAsync` insert/update/delete,
and a minimal hand-authored migration.

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
| `JSON` | raw `string` |

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
<PackageReference Include="DotRocks.Data" Version="1.0.0" />
<PackageReference Include="DotRocks.EntityFrameworkCore" Version="1.0.0" />
<PackageReference Include="DotRocks.EntityFrameworkCore.Design" Version="1.0.0" PrivateAssets="all" />
<PackageReference Include="DotRocks.Analyzers" Version="1.0.0" PrivateAssets="all" />
<PackageReference Include="DotRocks.Analyzers.CodeFixes" Version="1.0.0" PrivateAssets="all" />
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

Analyzer limits: diagnostics currently inspect source-visible constants, local string
assignments, `DotRocksConnectionStringBuilder` initializers, and local method bodies.
They do not perform interprocedural data-flow analysis, inspect runtime-built connection
strings, or prove transaction state across method boundaries.

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
    }
);
```

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

Licensed under the [MIT License](LICENSE).
