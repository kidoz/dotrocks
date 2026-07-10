# Observability

DotRocks emits OpenTelemetry-compatible tracing and metrics. This article is the complete
reference for the instrumentation surface: the activity source and meter names, every
metric and span tag, and what is deliberately **never** emitted.

> The authoritative definitions live in
> [`DotRocksTelemetry.cs`](https://github.com/kidoz/dotrocks/blob/main/src/DotRocks.Data/Diagnostics/DotRocksTelemetry.cs)
> and
> [`DotRocksTelemetryTags.cs`](https://github.com/kidoz/dotrocks/blob/main/src/DotRocks.Data/Diagnostics/DotRocksTelemetryTags.cs).
> This article mirrors that source.

## Wiring

Both tracing and metrics are exposed under the name **`DotRocks.Data`**:

```csharp
using DotRocks.Data.Diagnostics;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(DotRocksTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(DotRocksTelemetry.MeterName));
```

The public constants `DotRocksTelemetry.ActivitySourceName` and
`DotRocksTelemetry.MeterName` are the stable contract — subscribe with those names rather
than a hard-coded string.

## Metrics

All metrics are on meter `DotRocks.Data` (version `1.0.0`).

| Name | Kind | Unit | Description |
|---|---|---|---|
| `dotrocks.connections.opened` | Counter | `{connection}` | Physical/pooled connections opened. |
| `dotrocks.connection.open.duration` | Histogram | `ms` | Open duration (pool acquisition + physical open). |
| `dotrocks.transaction.duration` | Histogram | `ms` | Transaction begin→commit/rollback duration. |
| `dotrocks.commands.executed` | Counter | `{command}` | Commands executed. |
| `dotrocks.command.duration` | Histogram | `ms` | Command execution duration. |
| `dotrocks.pool.lease.wait_time` | Histogram | `ms` | Time spent waiting to acquire a pooled lease. |
| `dotrocks.pool.connections.discarded` | Counter | `{connection}` | Pooled connections discarded (not reused). |
| `dotrocks.stream_load.duration` | Histogram | `ms` | Stream Load request duration. |
| `dotrocks.stream_load.rows_loaded` | Counter | `{row}` | Rows loaded. |
| `dotrocks.stream_load.rows_filtered` | Counter | `{row}` | Rows filtered/rejected. |
| `dotrocks.stream_load.bytes` | Counter | `By` | Payload bytes accepted. |

Every metric tag is drawn from a bounded set — no high-cardinality labels are used:

| Metric | Tags |
|---|---|
| `dotrocks.connection.open.duration` | `outcome` ∈ `{success, timeout, error}` |
| `dotrocks.transaction.duration` | `outcome` ∈ `{committed, rolledback}` |
| `dotrocks.commands.executed`, `dotrocks.command.duration` | `outcome` ∈ `{success, error, canceled, timeout}`; `operation` = bounded SQL keyword or `OTHER` |
| `dotrocks.stream_load.*` | `outcome` ∈ `{success, error}` |
| `dotrocks.connections.opened`, `dotrocks.pool.*` | none |

## Tracing spans

DotRocks starts activities for connection-open and command execution. The span tags follow
the OpenTelemetry database semantic conventions:

| Tag (`activity.SetTag`) | Set on | Value |
|---|---|---|
| `db.system.name` | connection-open, command | `other_sql` (until a stable `starrocks` registry value exists) |
| `db.operation.name` | command | The leading SQL keyword (`SELECT`, `INSERT`, …) from a bounded set, or `OTHER` |
| `db.query.summary` | command | Equals the operation name — **no SQL text** |
| `db.namespace` | connection-open | The connection's `Database`, only when non-empty |
| `server.port` | connection-open | The configured query port |
| `db.response.status_code` | error span | StarRocks error code or a well-formed 5-char SQLSTATE |
| `error.type` | error span | Stable classification: `timeout`, `canceled`, exception type name, or SQLSTATE/code |

On failure the span status is set to `Error` and the error classification is recorded —
but **no raw exception message or server text** is ever attached.

## What is never emitted

The instrumentation is deliberately hardened against leaking secrets and inflating
cardinality. It **never** emits:

- Raw SQL text, query literals, or parameter values
- Connection strings, passwords, or usernames
- Table identifiers or object names (beyond the connection's `Database`)
- Server hostnames (a configured host may be tenant-bearing; `server.address` is omitted)
- Unbounded server-controlled strings — SQLSTATE values are validated to exactly 5 ASCII
  alphanumeric characters before use as a tag, so a hostile server cannot inflate label
  cardinality or smuggle arbitrary text

`db.query.summary` is intentionally the **operation keyword only**, never the query text.
This keeps it low-cardinality and free of sensitive content.

## See also

- [Security](security.md) — the secret-hygiene policy this instrumentation enforces
- [Connection strings](connection-strings.md)
- [Stream Load](stream-load.md) — the Stream Load metrics
