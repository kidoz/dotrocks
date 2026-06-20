# StarRocks 3.x Driver Developer Notes

Source snapshot: 2026-06-20.

This document summarizes StarRocks 3.x behavior that matters when building and
maintaining DotRocks against older lines. It is research guidance, not a
replacement for live compatibility tests. Treat observed behavior from a pinned
StarRocks 3.x image as authoritative when it differs from documentation. 3.x
support should mean live compatibility tests, documented feature gates, and
explicit unsupported errors when a 3.x server lacks a capability that exists in
4.x.

## Primary Baseline

DotRocks should target 3.x lines in this order, treating 3.5 as the only line
that gets near-parity with the current 4.0 baseline:

| Target | Driver status |
| --- | --- |
| 3.5 latest patch | First 3.x target. Near-parity with the 4.0 baseline. |
| 3.4 latest patch | Read/query, Stream Load, type, and EF DDL smoke target. SQL transactions and MySQL-protocol SSL should be feature-gated or explicitly unsupported unless live tests prove otherwise. |
| 3.3 latest patch | Same as 3.4, lower priority. Keep as compatibility smoke and query/load characterization. |
| 3.2 latest patch | Optional read/query target only. Prepared statements and HTTP SQL API begin here, but transaction/TLS behavior is not a safe baseline. |
| 3.1 and 3.0 | Best-effort only unless there is user demand. Do not promise full support. |

The StarRocks release guide says the project maintains the three latest minor
versions. With latest docs currently showing 4.1, 4.0, and Stable-3.5, DotRocks
should treat 3.5 as the production-relevant 3.x line and keep older 3.x support
as compatibility/backport scope.

3.5 adds several driver-visible capabilities over earlier 3.x lines:

- MySQL-protocol SSL support.
- External authentication expansion: OAuth 2.0, JWT, LDAP Security Integration,
  and group providers.
- Beta SQL transactions, but only for loading operations via `INSERT`.
- JDK 17 runtime requirement from 3.5.0 onward, which affects Docker images and
  CI startup.
- Stream Load transaction `prepared_timeout` from 3.5.4 onward.

## Wire Protocol

StarRocks uses a MySQL-compatible protocol for the FE query port, and DotRocks
must implement only the StarRocks subset it verifies on each 3.x line.

Driver support should be explicit for:

- Handshake protocol version 10.
- Capability negotiation used by StarRocks FE.
- Native password / `mysql_native_password` authentication.
- TLS upgrade only where the 3.x line and connection options support it.
- Text protocol command execution.
- Text protocol result sets, OK packets, ERR packets, EOF/OK result terminators,
  warnings, affected rows, and last insert id where surfaced.
- `KILL`-based or connection-close cancellation characterization.

Driver limitations should remain explicit for:

- Unsupported authentication plugins.
- Authentication switch requests if not implemented.
- MySQL features not verified against StarRocks 3.x.
- General-purpose MySQL compatibility.
- `LOAD DATA LOCAL INFILE`, unless deliberately implemented and threat-modeled.
- Stored procedures, multiple result sets, server cursors, and MySQL binary
  protocol extensions until verified.

## Authentication And TLS

StarRocks 3.x supports native password authentication and
`mysql_native_password`. From 3.5, StarRocks also documents OAuth 2.0, JWT, and
LDAP Security Integration plus group providers. Those external methods may
require clear-password or custom plugin behavior in MySQL clients, so DotRocks
should keep OAuth/JWT/LDAP paths as explicit unsupported failures until each
method is designed and tested.

TLS for MySQL-protocol connections is not a safe baseline before 3.5:

- On 3.5, TLS tests must include at least one patch image with a configured SSL
  FE before claiming MySQL-protocol TLS support.
- On 3.4/3.3, MySQL-protocol TLS availability is unknown and must be verified by
  live test or rejected.
- On 3.2 and earlier, treat TLS behavior as not a safe baseline.

Driver requirements where TLS is available:

- Default to a clearly documented security posture.
- Support TLS-required mode for non-local production use.
- Validate certificates by default.
- Keep a test-only trust-server-certificate escape hatch.
- Never leak passwords, Basic tokens, connection strings, JWTs, OAuth secrets, or
  Stream Load response bodies through exceptions, traces, debugger displays, or
  `ToString()`.

## SQL Execution

Text SQL is the safest execution path on every 3.x line. DotRocks should keep
named parameter binding as client-side literal formatting unless and until
StarRocks binary prepared statements are characterized per version.

Prepared statements are documented from 3.2 onward, but the `PREPARE` syntax
section still carries the SELECT-only caveat:

- Keep DotRocks `Prepare()` as client-side validation. Do not implement
  server-side prepared statements for 3.x until COM_STMT_PREPARE or SQL
  `PREPARE` behavior is characterized by version.
- Add live characterization before implementing server-side prepares.
- Never assume MySQL Connector/J behavior equals DotRocks protocol behavior.

HTTP SQL API is documented from 3.2.0, initially for internal tables only; from
3.2.1 it can query external catalogs. It should remain an optional future query
transport, not a replacement for the ADO.NET MySQL-protocol driver, and is not
required for ADO.NET support.

## Data Types

Recommended ADO.NET type mapping for verified scalar values matches the 4.x
mapping, with the decimal boundary as the key difference:

| StarRocks type | CLR target |
| --- | --- |
| `BOOLEAN` | `bool` |
| `TINYINT` | `sbyte` |
| `SMALLINT` | `short` |
| `INT` / `INTEGER` | `int` |
| `BIGINT` | `long` |
| `LARGEINT` | `Int128` |
| `FLOAT` | `float` |
| `DOUBLE` | `double` |
| `DECIMAL(p,s)` where exact .NET conversion fits | `decimal` |
| `DECIMAL(p,s)` where precision exceeds exact .NET decimal | `DotRocksDecimal` |
| `DATE` | `DateOnly` or `DateTime` depending on API |
| `DATETIME` | `DateTime` |
| `CHAR`, `VARCHAR`, `STRING` | `string` |
| `JSON` | raw `string` initially |
| `BINARY`, `VARBINARY` | `byte[]` when live verified |

Important 3.x type notes:

- `LARGEINT` is a 16-byte signed integer. Driver tests should cover min, max, and
  ordinary values.
- `DECIMAL256` is not available on 3.x. 3.x supports Fast DECIMAL / DECIMAL128
  with precision up to 38. `DECIMAL(76,s)` must be rejected or reported as
  unsupported rather than silently rounded.
- `BINARY` / `VARBINARY` is documented from 3.0 onward; 3.1 adds Fast Decimal
  support in complex types and generated columns.
- Binary types are not general string types, and Stream Load CSV uses hex input
  for binary values while JSON Stream Load does not support `BINARY`.
- `ARRAY`, `MAP`, `STRUCT`, `BITMAP`, `HLL`, and `VARIANT` should remain
  unsupported or raw-string/opaque until their result metadata and text
  encodings are characterized on each line.

## SQL Transactions

StarRocks SQL transactions are beta on 3.5 and not documented for the 3.4/3.3
data-loading table of contents. Do not assume SQL `START TRANSACTION` support
below 3.5.

3.5 SQL transaction limitations that DotRocks must preserve:

- Beta feature.
- Only `INSERT` and `SELECT` statements are supported inside SQL transactions.
- Multiple `INSERT` statements against the same table are not allowed.
- Target tables must be in the same database.
- Previous transaction writes are invisible to later statements in the same
  transaction.
- The target table of a previous `INSERT` cannot be a source table in later
  statements.
- No nested transactions.
- Only limited `READ COMMITTED` isolation.
- No write-conflict checks.

DotRocks requirements:

- `BeginTransaction()` can be supported only with a 3.5-specific transaction
  capability profile and documentation that it is not OLTP-style support.
- `UPDATE` and `DELETE` inside explicit transactions should be rejected or
  characterized as unsupported on 3.5.
- `DotRocksTransaction` should fail explicitly on 3.4/3.3/3.2 unless live tests
  prove the exact SQL transaction contract.
- Never return a pooled physical connection with an active transaction.
- Reject foreign, completed, or mismatched transaction objects immediately.
- Disable or reject savepoints.

## Stream Load

Stream Load is a separate HTTP API, not part of the MySQL protocol, and is
available across 3.x lines. It supports CSV and JSON payloads and should stream
request bodies without buffering the entire input.

Driver requirements:

- Use HTTP PUT for normal Stream Load.
- Use Basic authentication from connection options.
- Prefer HTTPS for non-local endpoints.
- Preserve request body streaming through FE-to-BE/CN redirects.
- Preserve credentials only when redirect policy is safe and intentional.
- Send or support `Expect: 100-continue` for large payloads.
- Handle payloads with known `Content-Length` and chunked transfer.
- Parse StarRocks JSON result objects into a stable result type.
- Treat HTTP failure bodies and load result messages as sensitive unless safely
  redacted.

CSV nulls are represented by `\N`; empty CSV fields are empty strings. Stream
Load does not support loading CSV data containing a JSON-formatted column.

## Stream Load Transactions

The Stream Load transaction interface is HTTP-based and exists on 3.4/3.3 (and
on 3.2 pending verification), supporting begin, load, prepare, commit, and
rollback.

On 3.x, Stream Load transactions are single-database, single-table only:

- Multi-table Stream Load transaction options must be rejected before sending
  HTTP requests.
- A label is required and must be the same for begin, load, prepare, and commit.
- `begin`, `load`, or `prepare` errors fail and automatically roll back the
  transaction.
- `prepared_timeout` is available from 3.5.4 onward.

DotRocks requirements:

- Transactional Stream Load can be supported, but only for single-table loads.
- Transaction objects must be single-use.
- Commit/rollback cancellation or I/O failure after dispatch must report an
  in-doubt outcome, after which the local object must reject further use.
- Redirect behavior must preserve streaming and credential safety.

## DDL And Table Shape

StarRocks table DDL is not interchangeable with generic relational DDL on any
3.x line.

Driver and EF provider requirements:

- Quote identifiers with backticks and escape embedded backticks.
- Preserve `@p...` placeholders in generated SQL where DotRocks parameter binding
  expects them.
- Generate StarRocks table shapes explicitly: key model, key columns,
  distribution columns, bucket count, and replication number.
- Default DDL should be conservative.
- Reject unsupported schema mutations before sending partial SQL.
- Do not pretend general EF migrations are supported.

StarRocks key models:

- `DUPLICATE KEY` is the default table type.
- `PRIMARY KEY` and `UNIQUE KEY` replace rows by key and support update/delete
  use cases.
- `AGGREGATE KEY` has aggregation-specific value-column behavior and should not
  be a default EF target.
- Table type cannot be modified after creation.

System and DDL limits:

- Object naming has strict allowed characters and length limits.
- StarRocks supports only UTF-8, not GBK.
- `FLOAT` and `DOUBLE` cannot be key columns.
- `enable_table_name_case_insensitive` is a 4.0+ cluster-creation-time option and
  should not be assumed on 3.x.

## EF Core Provider Boundaries

The EF provider should be even more conservative on 3.x than on 4.0:

- Query support can grow by verified translation slices.
- Writes should be single-table, explicit-primary-key, scalar-only, and
  parameterized.
- `SaveChanges` must remain one row per `SaveChanges` on 3.5 and should not
  assume 4.0 transaction behavior.
- On 3.4/3.3, EF writes should avoid explicit SQL transactions by default, or be
  rejected if the provider cannot safely execute the write pattern without them.
- Generated values, navigations, owned types, concurrency tokens, and
  composite-key writes should remain unsupported until designed.
- Savepoints must be disabled or rejected.
- Migrations should start with database creation, table creation/drop, and
  migration history only.
- DDL mutation operations such as add/drop/alter column, rename, foreign keys,
  indexes, defaults, computed columns, and destructive database rollback should
  be explicit failures unless implemented and live-tested.

## Observability And Cancellation

3.x lacks the 4.0 global connection IDs and richer query observability, so
DotRocks should not depend on them. The driver should:

- Expose sanitized connection and command diagnostics.
- Keep query ID / connection ID capture as a future capability and not rely on
  4.0-only identifiers.
- Cancel by closing the physical connection when protocol state cannot be
  recovered.
- Discard pooled connections after timeout, cancellation during I/O, malformed
  packets, partial reads, auth failure, or active-reader abandonment.
- Keep fake-server tests for malformed handshake, auth result, result metadata,
  OK/ERR packets, and row payloads.

## Capability Gates

DotRocks needs a runtime version/capability model before it can support multiple
StarRocks lines cleanly. Populate an internal capability profile from the
handshake server version plus live feature probes where the version string is
ambiguous:

| Capability | 4.0 baseline | 3.5 | 3.4/3.3 | 3.2 | 3.1/3.0 |
| --- | --- | --- | --- | --- | --- |
| Text protocol query | yes | verify | verify | verify | verify |
| MySQL-protocol TLS | yes | yes | unknown, verify or reject | unknown | unknown |
| Prepared statements | client-side only | client-side only | client-side only | client-side only | no claim |
| SQL transactions | broader 4.0 behavior | INSERT-only beta | reject unless verified | reject | reject |
| Stream Load CSV/JSON | yes | verify | verify | verify | verify |
| Stream Load transaction | multi-table same DB | single table | single table | verify single table | verify single table |
| DECIMAL256 | yes | no | no | no | no |
| DECIMAL128 | yes | yes | yes | yes | yes |
| VARBINARY | yes | yes | yes | yes | yes |
| HTTP SQL API | yes | yes | yes | yes from 3.2 | no claim |
| EF SaveChanges | constrained | further constrained | reject or no explicit tx | reject | reject |

Implementation work:

- Add `DotRocksServerVersion` parsing from handshake strings such as
  `8.0.33-StarRocks-3.5.x`.
- Add `DotRocksServerCapabilities` with flags for SQL transactions, SQL
  transaction DML set, Stream Load transaction multi-table support,
  `prepared_timeout`, TLS availability, DECIMAL256, and HTTP SQL API.
- Make capability checks happen before feature execution where possible.
- Use feature probes only for behavior that cannot be trusted from version
  strings.
- Surface unsupported capability failures as `NotSupportedException` or a
  specific DotRocks exception with sanitized messages.

## Version Compatibility Plan

Add CI/live matrix entries for:

| Version | Required tests |
| --- | --- |
| 3.5 latest patch | Full ADO.NET live suite minus 4.0-only features; EF query/DDL/write smoke; Stream Load and Stream Load transaction single-table; TLS smoke if image can be configured. |
| 3.4 latest patch | ADO.NET query/type/pooling/cancel; Stream Load CSV/JSON; Stream Load transaction single-table; EF query and DDL smoke; explicit SQL transaction unsupported tests. |
| 3.3 latest patch | Same as 3.4, lower priority. |
| 3.2 latest patch | Optional open/query/type/Stream Load smoke; prepared client-side validation; explicit unsupported tests for SQL transactions. |
| 3.1/3.0 | No CI gate initially; run manually only for requested support. |

For each 3.x minor/patch line, verify:

- Handshake server version parse and capability flags.
- Authentication plugin and auth switch behavior.
- TLS required/supported/unsupported behavior.
- `SELECT 1`, parameterized SELECT, Dapper `QuerySingleAsync<int>`, and common
  scalar mappings.
- `LARGEINT` min/max as `Int128`.
- `DECIMAL(38,4)` and rejection/no-support for `DECIMAL(76,4)`.
- `VARBINARY` and `BINARY` read/write, `HEX`, `to_binary`, `from_binary`.
- Stream Load CSV and JSON.
- Stream Load transaction commit/rollback with one table, and multi-table
  rejection.
- SQL transaction commit/rollback on 3.5 with the allowed `INSERT` shape, and
  unsupported/restricted behavior on 3.4/3.3/3.2.
- EF migrations table-shape DDL on 3.5/3.4/3.3.
- EF `SaveChanges` one-row behavior on 3.5, and explicit rejection or separate
  non-transactional characterization on 3.4/3.3.

## Source References

- StarRocks version release guide: https://docs.starrocks.io/docs/developers/versions/
- StarRocks 3.5 release notes: https://docs.starrocks.io/releasenotes/release-3.5/
- StarRocks 3.4 release notes: https://docs.starrocks.io/releasenotes/release-3.4/
- StarRocks 3.3 release notes: https://docs.starrocks.io/releasenotes/release-3.3/
- StarRocks 3.5 SQL transactions: https://docs.starrocks.io/docs/3.5/loading/SQL_transaction/
- StarRocks 3.5 Stream Load transactions: https://docs.starrocks.io/docs/3.5/loading/Stream_Load_transaction_interface/
- StarRocks 3.4 Stream Load transactions: https://docs.starrocks.io/docs/3.4/loading/Stream_Load_transaction_interface/
- StarRocks 3.3 Stream Load transactions: https://docs.starrocks.io/docs/3.3/loading/Stream_Load_transaction_interface/
- StarRocks prepared statements: https://docs.starrocks.io/docs/sql-reference/sql-statements/prepared_statement/
- StarRocks HTTP SQL API: https://docs.starrocks.io/docs/sql-reference/http_sql_api/
- StarRocks DECIMAL / DECIMAL256 boundary: https://docs.starrocks.io/docs/sql-reference/data-types/numeric/DECIMAL/
- StarRocks BINARY / VARBINARY: https://docs.starrocks.io/docs/sql-reference/data-types/string-type/BINARY/
