# StarRocks 4.x Driver Developer Notes

Source snapshot: 2026-06-20.

This document summarizes StarRocks 4.x behavior that matters when building and
maintaining DotRocks. It is research guidance, not a replacement for live
compatibility tests. Treat observed behavior from the pinned StarRocks image as
authoritative when it differs from documentation.

## Primary Baseline

DotRocks should target StarRocks 4.0 first, with 4.0.7+ as the practical test
baseline already used by the project. StarRocks 4.1 exists, but the first
compatibility surface should be 4.0.x because it introduced several driver-visible
changes:

- SQL transactions and Stream Load transactions gain broader multi-table behavior.
- `DECIMAL256` extends decimal precision beyond .NET `decimal` and beyond the
  existing `DotRocksDecimal` test boundary if only DECIMAL128 has been verified.
- `VARBINARY` MySQL-protocol response behavior has version-specific controls in
  4.0 patch releases.
- Case-insensitive object-name handling becomes a cluster-creation-time option.
- Global connection IDs and richer query observability affect cancellation,
  diagnostics, and test assertions.

## Wire Protocol

StarRocks uses a MySQL-compatible protocol for the FE query port, but DotRocks
must implement only the StarRocks subset it verifies.

Driver support should be explicit for:

- Handshake protocol version 10.
- Capability negotiation used by StarRocks FE.
- Native password / `mysql_native_password` authentication.
- TLS upgrade when server and connection options require it.
- Text protocol command execution.
- Text protocol result sets, OK packets, ERR packets, EOF/OK result terminators,
  warnings, affected rows, and last insert id where surfaced.
- `KILL`-based or connection-close cancellation characterization.

Driver limitations should remain explicit for:

- Unsupported authentication plugins.
- Authentication switch requests if not implemented.
- MySQL features not verified against StarRocks, even if common MySQL drivers
  support them.
- General-purpose MySQL compatibility.
- `LOAD DATA LOCAL INFILE`, unless implemented and threat-modeled.
- Stored procedures, multiple result sets from procedures, server cursors, and
  MySQL binary protocol extensions until verified.

## Authentication And TLS

StarRocks supports native password authentication and `mysql_native_password`.
StarRocks also documents LDAP, JWT, and OAuth 2.0 authentication methods. Those
external methods may require clear-password or custom plugin behavior in MySQL
clients, so DotRocks should fail explicitly until each method is designed and
tested.

TLS is available for MySQL-protocol connections from StarRocks 3.4.1 onward.
Driver requirements:

- Default to a clearly documented security posture.
- Support TLS-required mode for non-local production use.
- Validate certificates by default.
- Keep a test-only trust-server-certificate escape hatch.
- Never leak passwords, Basic tokens, connection strings, JWTs, OAuth secrets, or
  Stream Load response bodies through exceptions, traces, debugger displays, or
  `ToString()`.

## SQL Execution

Text SQL remains the default execution path. DotRocks uses client-side literal
formatting for named `@` parameters and exposes the characterized binary prepared
path through `DotRocksParameterMode.ServerPrepared`.

Prepared statements in StarRocks 4.0 are session-scoped. The SQL documentation
states that placeholders are `?`, variables are passed with `EXECUTE ... USING`,
and the prepared statement is dropped at session end. The syntax section says only
`SELECT` is currently supported as a preparable statement, while later examples
and JDBC notes mention broader usage. Treat this as a documentation conflict.
`SELECT`, `INSERT`, `UPDATE`, and `DELETE` must be tested independently; MySQL
Connector/J behavior is not DotRocks protocol behavior.

Characterized on StarRocks 4.0.7 (compatibility harness `--prepare-probe`):
`COM_STMT_PREPARE` is supported. `SELECT 1 AS one` returns 0 params / 1 column;
`SELECT ? + ? AS total` returns 2 params / 1 column with a non-zero statement id.
DotRocks does not negotiate `CLIENT_DEPRECATE_EOF`, so the prepare response is the
prepare-OK header, then parameter-definition packets + EOF, then column-definition
packets + EOF. `COM_STMT_PREPARE`, `COM_STMT_EXECUTE`, and `COM_STMT_CLOSE` are
implemented and wired to `DotRocksParameterMode.ServerPrepared`, verified end to end
against StarRocks 4.0.7. Fixed-width numerics and temporal values use their native
binary layout; decimals, dates, and other non-numeric parameters are sent as
`VAR_STRING` text, which the server casts to the placeholder type.

HTTP SQL API exists and streams newline-delimited JSON for `SELECT`, `SHOW`,
`EXPLAIN`, and `KILL`, one SQL query per HTTP request. It can be a future optional
query path, but it is not a replacement for the ADO.NET MySQL-protocol driver
unless DotRocks adds a separate HTTP execution mode.

## Data Types

Recommended ADO.NET type mapping for verified scalar values:

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

Important 4.x type notes:

- `LARGEINT` is 16-byte signed integer. Driver tests should cover min, max, and
  ordinary values.
- StarRocks 4.0 introduces `DECIMAL256` with precision above 38 and up to 76.
  DotRocks must not silently round or overflow into `decimal`.
- `DECIMAL256` limitations include no automatic precision scale-up, no window
  function support, and no Aggregate-table support.
- Binary types are not general string types. StarRocks documents `BINARY` as an
  alias of `VARBINARY`, with the same maximum length as `VARCHAR`.
- `VARBINARY` cannot be used as a partition key, bucketing key, dimension column,
  or in `ORDER BY`, `GROUP BY`, and `JOIN` clauses.
- Stream Load CSV uses hex input for binary values; JSON Stream Load does not
  support `BINARY`.
- `ARRAY`, `MAP`, `STRUCT`, `BITMAP`, `HLL`, and `VARIANT` should remain
  unsupported or raw-string/opaque until their MySQL-protocol result metadata and
  text encodings are characterized.

## SQL Transactions

StarRocks SQL transactions are documented as beta. From 4.0 onward, transaction
support is broader but still constrained:

- Transaction statements are `START TRANSACTION` / `BEGIN`, `COMMIT`, and
  `ROLLBACK`.
- Supported statements inside transactions are `SELECT`, `INSERT`, `UPDATE`, and
  `DELETE`.
- ACID behavior is documented only for limited `READ COMMITTED` isolation.
- A transaction is bound to one session.
- Transactions cannot be nested.
- All target tables in the transaction must be in the same database.
- `INSERT OVERWRITE` is not supported in SQL transactions.
- Subsequent DML cannot read uncommitted changes from earlier DML in the same
  transaction.
- SELECT against tables changed in the same transaction is not allowed.
- For 4.0, multiple INSERTs against one table are supported only in shared-data
  clusters; UPDATE and DELETE support is also shared-data-specific.
- Within one transaction, only one UPDATE or DELETE per table is allowed, and it
  must precede INSERT statements for that table.
- If the session ends, the active transaction is automatically rolled back.

DotRocks requirements:

- Never return a pooled physical connection with an active transaction.
- Reject foreign, completed, or mismatched transaction objects immediately.
- Disable or reject savepoints.
- Document that SQL transactions are not OLTP-compatible semantics.
- Keep live characterization tests for rollback behavior, because observed
  behavior can differ from user expectations.

## Stream Load

Stream Load is a separate HTTP API, not part of the MySQL protocol. It supports
CSV and JSON payloads and should stream request bodies without buffering the
entire input.

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

Stream Load limits and options that matter:

- Stream Load is recommended for a small number of files, each not exceeding 10 GB.
- CSV and JSON are supported. CSV nulls are represented by `\N`; empty CSV fields
  are empty strings.
- Stream Load does not support loading CSV data containing a JSON-formatted column.
- JSON request bodies have size-related limits and `ignore_json_size` can bypass a
  pre-check at memory risk.
- Labels provide at-most-once/idempotency behavior for loads.
- Merge Commit can merge homogeneous concurrent loads, but asynchronous mode does
  not guarantee success visibility at response time.

## Stream Load Transactions

The Stream Load transaction interface is HTTP-based and supports begin, load,
prepare, commit, and rollback operations.

StarRocks 4.0 adds single-database multi-table transaction support for this
interface. Current documented limits:

- Multi-database multi-table transactions are not supported.
- Only concurrent writes from one client are supported.
- Multiple `/api/transaction/load` calls are allowed, but parameters except
  `table` must match.
- CSV records must end with a row delimiter.
- `begin`, `load`, or `prepare` errors fail and automatically roll back the
  transaction.
- A label is required and must be the same for begin, load, prepare, and commit.
- Reusing an ongoing transaction label for begin fails the previous transaction.
- Multi-table transactions require `transaction_type: multi` on all involved
  operations.
- `prepared_timeout` is available from 3.5.4 onward.

DotRocks requirements:

- Transaction objects must be single-use.
- Commit/rollback cancellation or I/O failure after dispatch must report an
  in-doubt outcome.
- After in-doubt completion, local transaction objects must reject further use.
- Redirect behavior must preserve streaming and credential safety.

## DDL And Table Shape

StarRocks table DDL is not interchangeable with generic relational DDL.

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
- `PRIMARY KEY` and `UNIQUE KEY` replace rows by key and support update/delete use
  cases.
- `AGGREGATE KEY` has aggregation-specific value-column behavior and should not be
  a default EF target.
- Table type cannot be modified after creation.

System and DDL limits:

- Object naming has strict allowed characters and length limits.
- StarRocks supports only UTF-8, not GBK.
- `FLOAT` and `DOUBLE` cannot be key columns.
- `enable_table_name_case_insensitive` is a 4.0+ cluster-creation-time option and
  cannot be changed after cluster startup.

## EF Core Provider Boundaries

The EF provider should continue to be explicit and conservative:

- Query support can grow by verified translation slices.
- Writes should be single-table, explicit-primary-key, scalar-only, and
  parameterized.
- Generated values, navigations, owned types, concurrency tokens, and composite-key
  writes should remain unsupported until designed.
- Multi-row `SaveChanges` to one table is unsafe because StarRocks transaction
  semantics are not OLTP-like.
- Savepoints must be disabled or rejected.
- Migrations should start with database creation, table creation/drop, and
  migration history only.
- DDL mutation operations such as add/drop/alter column, rename, foreign keys,
  indexes, defaults, computed columns, and destructive database rollback should be
  explicit failures unless implemented and live-tested.

## Observability And Cancellation

StarRocks 4.0 includes global connection IDs and improved query observability.
DotRocks should:

- Expose sanitized connection and command diagnostics.
- Keep query ID / connection ID capture as a future capability.
- Cancel by closing the physical connection when protocol state cannot be recovered.
- Discard pooled connections after timeout, cancellation during I/O, malformed
  packets, partial reads, auth failure, or active-reader abandonment.
- Keep fake-server tests for malformed handshake, auth result, result metadata,
  OK/ERR packets, and row payloads.

## Version Compatibility Plan

Minimum useful matrix:

| Version | Driver status |
| --- | --- |
| 4.0.7+ | Primary supported test baseline. |
| 4.0 latest patch | Run compatibility smoke and protocol characterization before release. |
| 4.1.x | Track separately after 4.0 surface is stable. Beware downgrade and container-image notes in 4.1 release notes. |
| 3.5.x | Secondary/back-compat target only after 4.0 behavior is explicit. |

For each StarRocks minor/patch line, verify:

- Handshake version and capability flags.
- Authentication plugin.
- TLS behavior.
- Text protocol OK/ERR/result metadata.
- Common scalar type reads.
- `LARGEINT`, `DECIMAL(38,s)`, and `DECIMAL(76,s)` boundaries.
- `VARBINARY` result encoding.
- SQL transaction commit/rollback characterization.
- Stream Load CSV and JSON.
- Stream Load transaction commit, rollback, and in-doubt failure.
- EF table DDL and single-row write behavior.

## Source References

- StarRocks 4.0 release notes: https://docs.starrocks.io/releasenotes/release-4.0/
- StarRocks version release guide: https://docs.starrocks.io/docs/developers/versions/
- StarRocks 4.0 prepared statements: https://docs.starrocks.io/docs/4.0/sql-reference/sql-statements/prepared_statement/
- StarRocks SQL transactions: https://docs.starrocks.io/docs/loading/SQL_transaction/
- StarRocks Stream Load: https://docs.starrocks.io/docs/sql-reference/sql-statements/loading_unloading/STREAM_LOAD/
- StarRocks Stream Load transaction interface: https://docs.starrocks.io/docs/loading/Stream_Load_transaction_interface/
- StarRocks HTTP SQL API 4.0: https://docs.starrocks.io/docs/4.0/sql-reference/http_sql_api/
- StarRocks system limits: https://docs.starrocks.io/docs/sql-reference/System_limit/
- StarRocks SSL authentication: https://docs.starrocks.io/docs/administration/user_privs/ssl_authentication/
- StarRocks CREATE USER authentication options: https://docs.starrocks.io/docs/sql-reference/sql-statements/account-management/CREATE_USER/
- StarRocks data type overview 4.0: https://docs.starrocks.io/docs/4.0/category/numeric/
- StarRocks DECIMAL / DECIMAL256: https://docs.starrocks.io/docs/sql-reference/data-types/numeric/DECIMAL/
- StarRocks BINARY / VARBINARY: https://docs.starrocks.io/docs/sql-reference/data-types/string-type/BINARY/
- StarRocks CREATE TABLE 4.0: https://docs.starrocks.io/docs/4.0/sql-reference/sql-statements/table_bucket_part_index/CREATE_TABLE/
