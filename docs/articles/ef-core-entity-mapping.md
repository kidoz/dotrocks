# EF Core entity mapping

A reference for developers and AI agents mapping entities to StarRocks tables with the
DotRocks EF Core provider. It covers the two kinds of mapped entity, the up-front model
validation rules, the table-shape APIs used by migrations, and a fix-by-symptom catalog for
every validation error the provider can throw.

> Authoritative API surface lives in the
> [README EF Core section](../../README.md#entity-framework-core). This article expands it
> with the *why*, decision rules, and worked examples. When the two disagree, the README and
> the source (`DotRocksModelValidator`) win.

## The one rule that surprises everyone

**DotRocks validates the entire mapped model the first time the model is built — not at
`SaveChanges`.** The model is built lazily on first use: your first query, your first
`context.Model` access, even DI resolving `DbContextDependencies`. If any mapped entity
violates the write-safety rules, the whole `DbContext` fails to initialize and *every*
operation throws — including read-only `Where(...)` queries that never touch the offending
table.

The validator that enforces this is
[`DotRocksModelValidator`](../../src/DotRocks.EntityFrameworkCore/Infrastructure/DotRocksModelValidator.cs).

```text
System.NotSupportedException: DotRocks EF Core writable entity type
'MonthlyMetricSummary' requires a single-column primary key;
composite keys are not supported.
   at DotRocks.EntityFrameworkCore.Infrastructure.DotRocksModelValidator.ValidateEntityType(...)
   ...
   at MetricsService.GetMonthlySummaryAsync(...)   // a read-only query!
```

The query is innocent; the *model* is the problem. Fix the mapping, not the query.

## Two kinds of mapped entity

Every entity you map is exactly one of these. Choose deliberately.

| | Writable entity | Read-only / query entity |
|---|---|---|
| EF key | Single-column primary key | **No key** (`HasNoKey()`) |
| Use for | Tables you `INSERT`/`UPDATE`/`DELETE` via `SaveChanges` | Aggregates, reports, multi-column-key tables, anything you only read |
| StarRocks model | PRIMARY KEY table | DUPLICATE / AGGREGATE / UNIQUE / external catalog |
| Validation applied | Full write-safety rule set | Skipped entirely (see below) |
| `SaveChanges` | Supported (one row per call) | Not supported — query only |

The validator's first check is decisive: if `FindPrimaryKey()` returns `null`, the entity is
treated as read-only and **all** write-safety checks are skipped. `HasNoKey()` is therefore
the escape hatch for any table that does not fit the writable shape.

### Decision rule (use this first)

```
Do you call SaveChanges for this entity?
├─ No  → map it HasNoKey().  Done. No other restriction applies.
└─ Yes → it must be a StarRocks PRIMARY KEY table with a SINGLE-column key,
         scalar properties only, ValueGeneratedNever() on every property,
         no navigations, no composite/complex/binary/concurrency/generated columns.
```

## Read-only / query entities

This is the right mapping for reports, aggregates, and any StarRocks table whose key is not a
single column (DUPLICATE KEY and AGGREGATE KEY tables almost always have multi-column keys).

```csharp
public sealed class MonthlyMetricSummary
{
    public long EntityId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal TotalAmount { get; set; }
    public string UnitCode { get; set; } = string.Empty;
}

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<MonthlyMetricSummary>(entity =>
    {
        entity.HasNoKey();                              // <- opts out of write-model validation
        entity.ToTable("monthly_metric_summary");
        // Optional: entity.ToView("...") for a StarRocks view, or no table at all for
        // FromSqlRaw-only projection types.
    });
}
```

Now queries work and the context initializes:

```csharp
var summary = await context.Set<MonthlyMetricSummary>()
    .Where(row => row.EntityId == entityId && row.UnitCode == unitCode)
    .ToListAsync(cancellationToken);
```

What you give up by going keyless: change tracking, identity resolution, and `SaveChanges`.
That is exactly the intended trade for read-only data. If you later need to write the table,
it must first satisfy the writable rules below.

> **Mapping a result that has no table** (e.g. a `FromSqlRaw` DTO): map it
> `HasNoKey()` and either `ToView(...)`/`ToTable(...)` to name the source, or leave it
> table-less and only materialize it through `FromSqlRaw`/`SqlQuery`.

## Writable entities

A writable entity maps to a StarRocks **PRIMARY KEY** table and must satisfy every rule the
validator enforces. Violating any one throws at model-build time.

| Requirement | Why | Enforced by |
|---|---|---|
| Exactly one primary-key column | StarRocks single-column client-generated key is the only verified write key strategy | `primaryKey.Properties.Count != 1` |
| `ValueGeneratedNever()` on every property | DotRocks does not materialize server-generated values; keys are client-generated | `ValueGenerated != Never` |
| Scalar properties only — no navigations | No FK/JOIN/cascade semantics are modeled | `GetNavigations()/GetSkipNavigations()` |
| No complex/owned types | Not supported by the write pipeline | `GetComplexProperties()`, `IsOwned()` |
| No concurrency tokens / row versions | OLTP-style concurrency is not emulated | `IsConcurrencyToken` |
| No default/computed SQL | DotRocks does not emit generated DDL/values | `GetDefaultValueSql()/GetComputedColumnSql()` |
| No `byte[]` / `UInt128` / `binary` / `varbinary` | EF binary mapping is not verified end to end | property CLR/store-type check |

### Complete writable example

```csharp
public sealed class Widget
{
    public long Id { get; set; }          // single-column key
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public decimal Price { get; set; }
}

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Widget>(entity =>
    {
        entity.HasKey(w => w.Id);                       // single column
        entity.Property(w => w.Id).ValueGeneratedNever();   // client-generated
        entity.Property(w => w.Name).ValueGeneratedNever();
        entity.Property(w => w.IsActive).ValueGeneratedNever();
        entity.Property(w => w.Price).HasPrecision(20, 4).ValueGeneratedNever();

        entity.ToTable("widget");

        // Migration table shape (see next section). Writable tables must be PRIMARY KEY.
        entity.HasStarRocksPrimaryKey("Id")
              .HasStarRocksHashDistribution(buckets: 8, "Id")
              .HasStarRocksReplicationNum(3);
    });
}
```

### Writing: one row per `SaveChanges`

StarRocks rejects a second DML against a table already written in the same transaction
(error `5303`). Save one row per `SaveChanges` call, or use Stream Load for bulk.

```csharp
context.Widgets.Add(new Widget { Id = 101, Name = "prod-a", IsActive = true, Price = 9.99m });
await context.SaveChangesAsync();   // INSERT one row

var widget = await context.Widgets.FirstAsync(w => w.Id == 101);
widget.IsActive = false;
await context.SaveChangesAsync();   // UPDATE ... WHERE Id = @p — separate call
```

`SaveChanges` inside a user transaction works. StarRocks has no `SAVEPOINT`, so EF savepoints
are disabled, and DotRocks does not perform OLTP-style affected-row concurrency checks. For
bulk ingestion use Stream Load — see the README and the `DotRocks.Samples.StreamLoad` sample.

## Table-shape configuration for migrations

These fluent extensions live in `Microsoft.EntityFrameworkCore` (namespace import is the EF
one) and annotate the entity for the **migrations** SQL generator. They do not change query
behavior. Defaults when you configure nothing: `DUPLICATE KEY`, hash distribution by the key
columns, **one** bucket, `replication_num = 1`.

| API | Emits | Notes |
|---|---|---|
| `HasStarRocksDuplicateKey(params cols)` | `DUPLICATE KEY (...)` | Read-only model; pair with `HasNoKey()` entities |
| `HasStarRocksPrimaryKey(params cols)` | `PRIMARY KEY (...)` | Required for writable entities |
| `HasStarRocksUniqueKey(params cols)` | `UNIQUE KEY (...)` | |
| `HasStarRocksHashDistribution(int buckets, params cols)` | `DISTRIBUTED BY HASH (...) BUCKETS n` | `buckets` must be `> 0` |
| `HasStarRocksReplicationNum(int n)` | `PROPERTIES("replication_num" = "n")` | `n` must be `> 0` |

The column names passed here are **store column names** (the actual table columns), and they
must exist on the entity's table — the validator rejects unknown columns. When several
entities map to the same table, their table-shape annotations must not conflict.

```csharp
// A read-only DUPLICATE KEY table created by migrations and queried via a keyless entity.
modelBuilder.Entity<EventRow>(entity =>
{
    entity.HasNoKey();
    entity.ToTable("event_log");
    entity.HasStarRocksDuplicateKey("EventTime", "SourceId")
          .HasStarRocksHashDistribution(buckets: 16, "SourceId")
          .HasStarRocksReplicationNum(3);
});
```

Migrations are deliberately conservative: create database (`IF NOT EXISTS`), create/drop
table, and the EF history table. Column add/drop/alter/rename, rename table, indexes, FK,
defaults, computed columns, `DROP DATABASE`, and idempotent scripts are **not** generated.

## Validation error catalog (fix by symptom)

Every message below is thrown by `DotRocksModelValidator` at model-build time. The fix is in
`OnModelCreating`.

| Message contains | Cause | Fix |
|---|---|---|
| `requires a single-column primary key; composite keys are not supported` | A keyed entity has a multi-column PK | Make it read-only with `HasNoKey()`, or reduce to a single-column PK if it must be writable |
| `does not support owned entity type` | `OwnsOne/OwnsMany` mapping | Flatten owned data into scalar columns, or don't map it |
| `must contain scalar properties only; navigations are not supported` | A reference/collection navigation on a keyed entity | Remove the navigation; model relationships by querying with explicit keys |
| `must contain scalar properties only; complex properties are not supported` | `ComplexProperty(...)` | Flatten into scalar columns |
| `does not support concurrency token property` | `IsConcurrencyToken()` / `IsRowVersion()` | Remove it; DotRocks does not emulate OLTP concurrency |
| `requires explicit non-generated values; configure ... ValueGeneratedNever()` | A property left as DB-generated (default for keys) | Call `.ValueGeneratedNever()` on the property |
| `does not support generated/default SQL` | `HasDefaultValueSql()` / `HasComputedColumnSql()` | Remove it; set values in code |
| `does not support property type ...` (binary) | `byte[]`, `UInt128`, or `binary`/`varbinary` store type | Avoid EF binary mapping; use ADO.NET `GetBytes` if you must read bytes |
| `migrations do not support StarRocks table key model` | Invalid value on the `KeyModel` annotation | Use `HasStarRocksDuplicateKey/PrimaryKey/UniqueKey` |
| `cannot use unknown store column ... in the StarRocks ... clause` | Key/distribution column name not on the table | Use the actual store column name |
| `require at least one non-empty ... column` | Empty/blank column list | Pass real column names |
| `require ... bucket count / replication number ... greater than zero` | Non-positive `buckets`/`replicationNum` | Pass a value `> 0` |
| `do not support conflicting ... annotations on shared table` | Two entities on one table disagree on table shape | Make the annotations identical, or split the tables |

## Related analyzer diagnostics

`DotRocks.Analyzers` flags many of these at **build time**, before the runtime throw. Treat
them as the early-warning version of the catalog above (codes per the implementation brief;
verify the active set in your installed analyzer release):

| Code | Flags |
|---|---|
| `DTR2003` | Unsupported generated-value / identity configuration |
| `DTR2004` | Writable entity lacks an explicit StarRocks table model |
| `DTR2005` | StarRocks table model lacks required distribution configuration |
| `DTR2006` | Unsupported row-version / concurrency-token configuration |
| `DTR2007` | Unsupported foreign-key / cascade assumption |
| `DTR2008` | `SaveChanges` against a statically known read-only table model |

## AI-agent checklist

When mapping or debugging an entity for DotRocks, verify in order:

1. **Is it ever written via `SaveChanges`?** If not → `HasNoKey()` and stop. This resolves
   most `NotSupportedException` model-build failures, including composite keys.
2. If written: single-column `HasKey`, `ValueGeneratedNever()` on **every** property, no
   navigations/complex/owned/concurrency/generated/binary members.
3. Writable tables use `HasStarRocksPrimaryKey(...)`; read-only tables use
   `HasStarRocksDuplicateKey/UniqueKey(...)`.
4. Distribution/key columns are real **store column names** and exist on the table.
5. Writes are one row per `SaveChanges`; bulk goes through Stream Load.
6. Remember validation is whole-model and up-front: a single bad entity breaks read-only
   queries on unrelated tables. Fix the mapping, not the query.
