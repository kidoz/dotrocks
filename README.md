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
| `DotRocks.Analyzers` | Roslyn analyzers (and code fixes) for correct, secure DotRocks usage. |

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

Unsupported EF Core behavior is explicit:

- `SaveChanges`, `ExecuteUpdate`, and `ExecuteDelete`.
- `EnsureCreated`, schema creation/deletion, and migrations.
- joins, `Include`, navigation materialization, and `GroupBy`.
- binary/varbinary mapping until StarRocks binary wire behavior is verified.
- `LARGEINT` / `Int128` until the ADO.NET reader has a verified Int128 path.

EF Core type mapping:

| StarRocks type | EF CLR type |
| --- | --- |
| `BOOLEAN` | `bool` |
| `TINYINT` | `sbyte` |
| `SMALLINT` | `short` |
| `INT`, `INTEGER`, `MEDIUMINT` | `int` |
| `BIGINT` | `long` |
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

## Stream Load

Use `DotRocksStreamLoadClient` for StarRocks HTTP Stream Load. The client uses the same
connection string credentials and reads payloads from the supplied stream.

```csharp
using DotRocks.Data.Loading;

using var client = new DotRocksStreamLoadClient(
    "Server=127.0.0.1;Port=9030;User ID=root;Stream Load Endpoint=http://127.0.0.1:8030"
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
    "Server=127.0.0.1;Port=9030;User ID=root;Stream Load Endpoint=http://127.0.0.1:8030"
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
commands above cover the server-free unit and protocol tests.

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
