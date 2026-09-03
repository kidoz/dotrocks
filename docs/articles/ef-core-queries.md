# EF Core query translation

A reference for what the DotRocks EF Core provider translates to StarRocks SQL: the LINQ
operators and BCL members it recognizes, the NULL semantics that differ from other providers,
the raw-SQL entry points, and the query shapes it deliberately refuses.

> Authoritative behavior lives in the translators under
> [`src/DotRocks.EntityFrameworkCore/Query`](https://github.com/kidoz/dotrocks/tree/main/src/DotRocks.EntityFrameworkCore/Query)
> and is pinned by `DotRocksTranslatorTests`, `DotRocksJoinAndAggregateQueryTests`, and
> `DotRocksRawSqlQueryTests`. This article mirrors that source. When the two disagree, the
> source wins.

For mapping entities to tables, see [EF Core entity mapping](ef-core-entity-mapping.md).

## Translated LINQ surface

| Area | Translates | Notes |
|---|---|---|
| Filtering | `Where`, comparison operators, `&&` / `\|\|`, nullable comparisons | Captured scalars render as `@`-prefixed placeholders named after the variable, with values carried in `DbParameter`s |
| Sets | `Contains` over a constant or parameter collection | Emits `IN (...)` |
| Ordering / paging | `OrderBy`, `ThenBy`, `OrderByDescending`, `Skip`, `Take`, `Distinct` | See the `LIMIT`/`OFFSET` note below |
| Execution | `FirstOrDefaultAsync`, `SingleAsync`, `ToListAsync`, `CountAsync`, `AnyAsync` | |
| Strings | `StartsWith`, `EndsWith`, `Contains` | `LIKE` with backslash-escaped wildcards; StarRocks rejects an `ESCAPE` clause, so none is emitted |
| Aggregates | `Min`, `Max`, `Sum`, `Average`, plus conditional aggregation | See [Conditional aggregation](#conditional-aggregation) |
| Grouping | `GroupBy` with key projection, `HAVING` predicates | |
| Joins | `Join`, `GroupJoin`/`SelectMany` + `DefaultIfEmpty`, cross joins | `INNER JOIN` / `LEFT JOIN` / `CROSS JOIN` |
| Math | `Abs`, `Ceiling`, `Floor`, `Round`, `Sqrt`, `Exp`, `Log`, `Sign`, `Pow` | → `abs`, `ceil`, `floor`, `round`, `sqrt`, `exp`, `ln`, `sign`, `power` |
| Date/time members | `Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `DayOfYear`, `.Date` | → `year()` … `dayofyear()`, `date()`; `DateOnly` supports the date components |
| Date/time arithmetic | `AddYears` … `AddSeconds` | → `years_add` … `seconds_add` (plain-argument form, no `INTERVAL` syntax); `DateOnly` supports years/months/days |
| Extrema | `EF.Functions.Greatest`/`Least`, `Math.Max`/`Math.Min`, inline-collection `Max()`/`Min()` | → `greatest()` / `least()`; see [NULL semantics](#greatest--least-and-mysql-null-semantics) |
| Projection | Anonymous objects and simple DTOs | |

Two translations fall back to EF's normal "could not be translated" failure rather than emit
wrong SQL: `Math.Round(value, MidpointRounding)` (StarRocks has no equivalent) and the
`AddX(double)` overloads with a fractional constant (the StarRocks `*_add` functions take a
whole-number count). A count that is only known at execution time — a parameter, including an
`int` variable, which EF passes as a `double` parameter, or a column — is guarded with
`assert_true`, so a fractional value fails the query on the server with a clear message instead
of being truncated to a whole count.

### `LIMIT` before `OFFSET`

StarRocks rejects a bare `OFFSET`. A `Skip` without a `Take` therefore emits a synthetic
unbounded limit:

```sql
SELECT `m`.`id`
FROM `measurements` AS `m`
ORDER BY `m`.`id`
LIMIT 9223372036854775807
OFFSET @p
```

### Null-valued parameters

When a captured parameter is `null`, an `==` / `!=` comparison against it is rewritten to
`IS NULL` / `IS NOT NULL` instead of a `= NULL` comparison that would never match.

## `Greatest` / `Least` and MySQL NULL semantics

Both the EF-standard relational overloads (params array, any argument count) and the DotRocks
2–4 argument overloads translate to the native functions, as do `Math.Max`/`Math.Min` and
inline-collection `Max()`/`Min()`:

```csharp
using Microsoft.EntityFrameworkCore;

var rows = await context.Measurements
    .Where(m => EF.Functions.Greatest(m.LastSeenAt, m.LastReportedAt) >= since)
    .ToListAsync(cancellationToken);
// WHERE greatest(`m`.`last_seen_at`, `m`.`last_reported_at`) >= @since

var largest = await context.Measurements
    .Select(m => new[] { m.SensorA, m.SensorB, m.SensorC }.Max())
    .ToListAsync(cancellationToken);
// SELECT greatest(`m`.`sensor_a`, `m`.`sensor_b`, `m`.`sensor_c`)
```

> **NULL semantics differ from PostgreSQL and SQL Server.** StarRocks follows MySQL:
> `greatest()`/`least()` return `NULL` when **any** argument is `NULL`. PostgreSQL and SQL
> Server ignore NULL arguments. A query migrated verbatim from Npgsql will silently drop rows
> whose comparison columns are nullable.

DotRocks does not offer a NULL-ignoring variant — the translation encodes MySQL semantics in
its nullability annotations, and hiding the difference behind a helper would make the emitted
SQL harder to reason about. Where you need PostgreSQL-style behavior, write it explicitly:

```csharp
EF.Functions.Greatest(
    m.LastSeenAt ?? DateTime.MinValue,
    m.LastReportedAt ?? DateTime.MinValue)
// greatest(COALESCE(`m`.`last_seen_at`,     TIMESTAMP '0001-01-01 00:00:00.0000000'),
//          COALESCE(`m`.`last_reported_at`, TIMESTAMP '0001-01-01 00:00:00.0000000'))
```

`EF.Functions.Greatest`/`Least` throw `InvalidOperationException` when invoked on the client —
they exist only to be translated.

## Conditional aggregation

`Sum` over a conditional expression translates to `SUM(CASE WHEN ... END)`, `??` over an
aggregate result to `COALESCE(...)`, and `Math.Abs` to `abs(...)`. Several measures therefore
compute in one grouped query over one scan, instead of one round trip per measure:

```csharp
var totals = await context.Measurements
    .Where(m => m.DeviceId == deviceId)
    .GroupBy(m => 1)
    .Select(group => new
    {
        Inbound = Math.Abs(
            group.Sum(m => m.Kind == "input" ? (decimal?)m.Value : null) ?? 0),
        Outbound = group.Sum(m => m.Kind == "output" ? (decimal?)m.Value : null) ?? 0,
    })
    .SingleAsync(cancellationToken);
```

```sql
-- @deviceId='7'
SELECT abs(COALESCE(SUM(CASE
    WHEN `m0`.`kind` = 'input' THEN `m0`.`value`
END), 0.0)) AS `Inbound`, COALESCE(SUM(CASE
    WHEN `m0`.`kind` = 'output' THEN `m0`.`value`
END), 0.0) AS `Outbound`
FROM (
    SELECT `m`.`kind`, `m`.`value`, 1 AS `Key`
    FROM `measurements` AS `m`
    WHERE `m`.`device_id` = @deviceId
) AS `m0`
GROUP BY `m0`.`Key`
```

One scan, both measures. The constant discriminators (`'input'`, `'output'`) are inlined as
literals because they are constants in the expression tree; the captured `deviceId` becomes a
parameter named after the variable.

Project the conditional to a **nullable** value type (`(decimal?)m.Value : null`). That is the
shape EF recognizes as `SUM(CASE WHEN ...)`; a non-nullable `: 0` branch sums zeros instead of
skipping rows, which changes `AVG` and `COUNT`-style measures.

## Raw SQL

Every EF-standard raw-SQL entry point works and parameterizes captured values automatically —
no hand-built `DotRocksParameter` objects are needed.

| Form | Use for |
|---|---|
| `DbSet<T>.FromSql($"... {value} ...")` | Entity materialization, interpolated |
| `DbSet<T>.FromSqlRaw("... {0} ...", value)` | Entity materialization, positional placeholders |
| `Database.SqlQuery<T>($"... {value} ...")` | Scalar or DTO results outside the model |
| `Database.ExecuteSqlRawAsync(...)` | Commands that return no rows |

```csharp
var rows = await context.Measurements
    .FromSql($"SELECT * FROM measurements WHERE device_id = {deviceId} AND kind = {kind}")
    .ToListAsync(cancellationToken);
// SELECT * FROM measurements WHERE device_id = @p0 AND kind = @p1
```

Interpolated values become parameters, not inlined literals — the interpolation is captured as
a `FormattableString`, so this is safe against injection. That safety applies to the EF raw-SQL
APIs only: string interpolation assigned to `DotRocksCommand.CommandText` is flagged by
analyzer `DTR0009`, since ADO.NET has no equivalent capture. See [Analyzers](analyzers.md).

A result type materialized only through raw SQL is mapped `HasNoKey()` — see
[Mapping a result that has no table](ef-core-entity-mapping.md#read-only--query-entities).

## Not translated

These fail explicitly rather than degrading to client evaluation or approximate SQL:

- `ExecuteUpdate` / `ExecuteDelete` — StarRocks does not accept the `UPDATE`/`DELETE` shapes
  EF Core produces for arbitrary key models. Throws
  `NotSupportedException: DotRocks EF Core query translation for LINQ UPDATE is not implemented yet.`
  Analyzer `DTR0006` flags the call at build time. Use single-row `SaveChanges` or raw SQL.
- `EnsureCreated` / `EnsureDeleted` (`DTR0005`) — use migrations.
- Navigations on keyed entities — the model validator rejects them outright, so there is
  nothing to `Include`. Model relationships by joining explicitly on key columns.
- Owned entity types (`OwnsOne`/`OwnsMany`), for keyed and keyless entities alike.

## See also

- [EF Core entity mapping](ef-core-entity-mapping.md)
- [Getting started](getting-started.md)
- [Connection strings](connection-strings.md)
- [Analyzers](analyzers.md)
