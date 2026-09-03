# Changelog

All notable changes to DotRocks are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The package
version is derived from the release tag at publish time.

## [Unreleased]

### Changed
- The live integration matrix — CI and the local `just starrocks-up` default — moves from
  StarRocks 3.5.5 and 4.0.7 to 3.5.21 and 4.1.4, the latest 3.x and 4.x images. Every driver,
  Dapper, EF Core, and Flight SQL integration suite passes on both. One characterization changed:
  over the MySQL protocol, 4.1 types `ARRAY`/`MAP`/`STRUCT` results as `STRING` where 3.5 and 4.0
  send `VAR_STRING` (the JSON-formatted text is identical, and `DotRocksJson` reads both).
- EF Core models that declare an alternate key, a check constraint, or a foreign key (with or
  without navigation properties) are rejected at model validation, and migrations refuse a
  `CREATE TABLE` that carries a unique, check, or foreign-key constraint as well as the standalone
  add/drop unique, check, and foreign-key operations. StarRocks enforces none of these; previously
  the generated `CREATE TABLE` dropped them silently, leaving EF's uniqueness and referential
  assumptions unenforced without any error.
- `DateTime.AddDays`/`AddHours`/`AddMinutes`/`AddSeconds` with a fractional constant are no longer
  translated — EF reports the expression as untranslatable, as the documentation already stated —
  and a fractional parameter or column value now fails the query on the server with a clear
  `assert_true` message. Previously the count was cast to an integer, so `AddHours(1.5)` silently
  became one hour and returned the wrong rows. Whole-number values, including `int` variables
  (which EF passes as `double` parameters), translate as before.

### Fixed
- `DotRocksDataReader.NextResult()` rebuilds the name-to-ordinal lookup for the next result set.
  Previously the lookup built for the first result set was reused, so `reader["a"]` on a batch's
  second result returned the value at the first result's position for `a` — silently the wrong
  column when the column order differed — or an out-of-range error instead of "column not found".
- A socket that drops or a packet that is cut short while rows are streaming now surfaces from
  `Read`/`ReadAsync` as a `DotRocksException` (transient when the server closed the connection),
  matching what the same failure produces during command submission. Previously the row loop
  rethrew the raw `IOException`, or an internal parser exception type callers could not name, and
  `IsTransient` was unavailable for retry decisions.
- Cancelling the token passed to `ExecuteReaderAsync` after the result set has been fully read no
  longer aborts the connection. The abort hook stayed registered until the reader was disposed, so
  a request-abort token firing between the last `ReadAsync` and disposal discarded a healthy
  pooled socket and left the `DotRocksConnection` closed.
- EF Core inlined `DateTime` constants — `EF.Constant(...)`, or a `Contains` over a date
  collection — as `TIMESTAMP '…'` with seven fractional digits, which StarRocks does not parse
  ("Column 'TIMESTAMP' cannot be resolved"). The provider now emits the StarRocks `DATETIME '…'`
  literal with microsecond precision, and `TimeOnly` constants as a quoted string rather than the
  unsupported `TIME '…'` form. `DateOnly` already used the valid `DATE '…'` literal.
- Flight SQL: an advertised endpoint location with an unsupported scheme (for example a Unix
  socket), a path, or no host no longer fails the whole read; it is skipped in favor of the
  remaining trusted locations, exactly like an untrusted address. A failure while opening an
  endpoint stream now disposes the opened call instead of leaking it.
- Flight SQL: the opt-in MySQL-protocol read fallback applies only when statement discovery
  (`GetFlightInfo`, where StarRocks executes the statement) fails. A failure while fetching the
  result (`DoGet`) now surfaces the Flight error instead of re-executing the statement text over
  MySQL, which for anything but a pure read would have run it twice.

## [1.5.0] - 2026-08-25

### Changed
- Dependencies moved to their latest stable versions. The EF Core provider packages now require
  `Microsoft.EntityFrameworkCore.*` 10.0.11, and the Flight SQL transport requires
  `Grpc.Net.Client`/`Grpc.Core.Api` 2.83.0. The analyzers and code fixes build against Roslyn
  5.9.0, which raises the minimum compiler that loads them — older SDK bands skip the analyzers
  with a compiler-version warning instead of running them.
- The repository builds with the .NET SDK pinned at 10.0.400 (rolling forward across feature
  bands instead of failing when only a newer band is installed), and `dotnet test` runs in the
  Microsoft.Testing.Platform mode of the .NET 10 SDK — required by xunit.v3 4.x, which no longer
  supports the classic VSTest target. Test projects are passed as `--project` and the solution as
  `--solution`.

### Fixed
- The MySQL protocol materializes `DECIMAL` columns by their declared precision, matching the
  Arrow Flight SQL transport and the documented mapping: `GetValue`, `GetFieldType`, and the
  column schema report `decimal` when every value of the column converts exactly (precision
  ≤ 28) and `DotRocksDecimal` only for wider columns. Previously every decimal — however
  narrow — surfaced as `DotRocksDecimal` from the untyped reader surface, so generic row
  mappers that pass `GetValue` results through `Convert.ChangeType` failed with
  `InvalidCastException: Object must implement IConvertible` on ordinary money columns. The
  precision derives from the StarRocks wire metadata (column length = precision + 3, plus one
  when the scale is non-zero; verified live on StarRocks 3.5.5 and 4.0.7 for cast expressions,
  table columns, and aggregate-widened results). Typed access is unchanged in both directions:
  `GetDecimal`/`GetFieldValue<decimal>` unwrap a wide value when it fits, and
  `GetFieldValue<DotRocksDecimal>` wraps a narrow one.
- `DotRocksDecimal` implements `IConvertible`, so a wide `DECIMAL` read through `GetValue`
  also survives `Convert.ChangeType` and the `Convert.To*` family. Conversions stay lossless:
  `decimal` conversion throws `DotRocksPrecisionLossException` rather than rounding, integral
  conversions round half to even exactly like `System.Decimal` and throw `OverflowException`
  outside the target range, floating-point conversions parse the exact invariant text form to
  the correctly rounded value, and unsupported targets (`char`, `DateTime`) throw
  `InvalidCastException`.

## [1.4.2] - 2026-08-01

### Added
- A "Reading large results" guide documents how result sets stream, what bounds memory and time
  while reading, how to choose between the MySQL protocol and Arrow Flight SQL, and the
  capabilities DotRocks deliberately does not expose because StarRocks does not provide them —
  there is no fetch size or server-side cursor (StarRocks does not implement `COM_STMT_FETCH`)
  and no parallel Flight endpoint fan-out (StarRocks returns a single endpoint per query).
- `DotRocksDataReader` implements `GetStream` and `GetTextReader`, so code written against the
  standard ADO.NET large-value accessors works without falling back to the base implementations.
  Both read from the already-materialized field value (a NULL yields an empty stream/reader);
  the reader's memory guarantee remains per row, not per field, and that is now stated on the
  type along with the fact that `CommandBehavior.SequentialAccess` is accepted but imposes no
  restrictions and yields no additional memory benefit.
- The read path's per-row cost is now guarded by the performance budget. A server-free
  `MaterializeRows` benchmark drives rows through the typed `DotRocksDataReader` accessors
  (measured at ~478 bytes per row for a three-column row) with a tight allocation ceiling, and
  the budgeted benchmark suite runs in CI, so a regression in per-row buffering or boxing fails
  the build instead of going unnoticed. The live large-result test now also bounds allocation
  per row across the whole 100k-row drain and asserts the reader retains nothing afterwards,
  rather than only checking that opening the reader does not buffer.
- `DotRocksFlightSqlDataSource.CreateConnection` hands out ADO.NET connections that share the
  data source's channels and authenticated sessions, so short-lived connections no longer each
  build a private channel and session. The data source also implements `IAsyncDisposable`, which
  is the preferred way to dispose it because releasing server sessions is a network operation.
- `DotRocksFlightSqlTransaction.IsCompleted` reports whether the server confirmed completion.

### Changed
- The result-row loop reads each row into a buffer rented from `ArrayPool` and returns it once
  the row is decoded, instead of allocating a fresh array per row. Measured on the budgeted
  benchmark, per-row allocation drops from about 478 to 425 bytes for a narrow three-column row,
  and the saving grows with row width. This is safe because every decoded value copies out of
  the payload; a regression test poisons a recycled buffer to prove no materialized value aliases
  it. A row spanning continuation packets (a value larger than one 16 MB packet) still uses an
  exact-size array, since its length is not known until reassembly completes.
- `GetOrdinal` uses a lookup built once per result set rather than scanning the column list on
  every call, so reading columns by name inside a row loop is no longer O(columns) per access.
  The typed accessors (`GetInt32`, `GetInt64`, `GetDouble`, and friends) now test the boxed value
  directly before falling back to `Convert`.
- The Flight SQL benchmarks establish their connections once in setup rather than per iteration,
  and the direct record-batch benchmark now projects and consumes the same columns as the row
  benchmarks, so the comparison measures row materialization rather than a smaller projection
  plus per-iteration connection setup. `just integration-test` no longer runs the Flight SQL
  suite twice.

### Fixed
- A result value larger than the reader's maximum logical packet size now reports that limit
  instead of "StarRocks returned malformed protocol bytes", which sent callers looking for a
  protocol bug when the real cause was an oversized field.
- Arrow Flight SQL no longer leaks a StarRocks session per RPC. The transport sent Basic
  credentials on every call, and StarRocks creates a frontend session for each authenticated
  call, so a single query left two sleeping sessions behind and a benchmark run reached the
  1024-connection user limit (`ResourceExhausted`). The credentials are now exchanged once
  during the Flight handshake for the session bearer token that every later call reuses, and
  disposal releases the session with the Flight SQL `CloseSession` action. Measured against
  StarRocks 4.0.7: five queries created eleven sessions before the fix and none after it.
  Servers that do not implement the handshake keep the previous per-call behavior, and servers
  without the `CloseSession` action (StarRocks 3.5) hold one session per data source until it
  expires instead of one per call.
- A Flight SQL transaction is marked completed only after the server confirms completion, so a
  failed `CommitAsync` or `RollbackAsync` leaves it active and recoverable instead of stranding
  an open server transaction that the client can neither retry nor roll back. A failed rollback
  during disposal now also releases the connection, which previously stayed bound to a
  transaction that could no longer be completed.
- Flight SQL decimals report the type they actually materialize. Columns declared with more
  precision than `System.Decimal` can hold (`DECIMAL(38, s)` is routine in StarRocks) are typed
  as `DotRocksDecimal`, and `GetDecimal`/`GetFieldValue<decimal>` convert such values when they
  are representable instead of failing with an `InvalidCastException` from `Convert.ChangeType`.
- Cancelling a Flight SQL command raises `OperationCanceledException` rather than a raw
  `RpcException`, so consumers can tell cancellation from a transport failure. `Cancel()` no
  longer races with the end of execution, and no longer misses a cancellation issued in the
  instant after execution starts.
- `DbDataReader.HasRows` reports the real result. StarRocks does not declare a record count, and
  the reader previously answered `true` for every such result, including empty ones; a command
  now fetches the first batch before returning the reader.
- `GetOrdinal` and ordinal-based accessors report unknown columns with `IndexOutOfRangeException`
  as ADO.NET specifies, instead of `ArgumentOutOfRangeException`.
- Reading a large Flight SQL value in chunks through `GetBytes`/`GetChars` no longer
  re-materializes the whole value for every chunk; the materialized value is cached for the
  current row and column, so allocation is proportional to the value rather than to value size
  times chunk count.
- A Flight endpoint that advertises several locations no longer fails when the first one is
  untrusted or unreachable: the trusted alternatives are tried in the order the server supplied
  them.

## [1.4.1] - 2026-08-01

### Fixed
- `CommandTimeout` and `DbCommand.Cancel()` now apply while a `DbDataReader` iterates rows.
  The command's cancellation scope previously ended when the reader was handed back, so a
  server that stopped sending mid-result left `Read`/`ReadAsync` waiting indefinitely — with
  no timeout and no way to cancel — on both the synchronous and asynchronous paths. The
  reader now owns the scope and re-arms the timeout around each row fetch, so a stalled fetch
  fails with a timeout while a legitimately long streaming scan is not capped by one total
  budget. A timed-out or cancelled read retires the connection instead of returning it to the
  pool.
- Disposing a reader over a partially consumed result set no longer drains the remaining rows
  uninterruptibly. The courtesy drain that keeps a connection poolable is now bounded by the
  command timeout; on expiry the connection is retired rather than blocking the caller for the
  rest of the stream.

## [1.4.0] - 2026-08-01

### Added
- An experimental `DotRocks.FlightSql` package provides a separate Arrow Flight SQL transport. It
  streams native `Apache.Arrow.RecordBatch` results, executes standard Flight SQL statement
  updates, exposes async ADO.NET reader/command/transaction types, and can drive the existing EF
  Core provider through its `DbConnection` overload. Named parameters reuse DotRocks's safe SQL
  binder. Optional MySQL-protocol fallback is explicit: reads retry only safe discovery failures,
  while writes are routed before Flight so an ambiguous write is never replayed. Exact endpoint
  scheme, host, and port combinations remain allowlisted before credentials or tickets are
  forwarded. In-process protocol coverage,
  live StarRocks 3.5.5/4.0.7 Flight-read and fallback-write integration coverage, and comparative
  transport benchmarks are included. The standard Flight update path is retained for compatible
  endpoints; StarRocks 4.0.7 returns `UNIMPLEMENTED` for statement `DoPut` in live validation.

## [1.3.5] - 2026-07-31

### Added
- EF Core: the EF-standard relational `EF.Functions.Greatest`/`Least` params-array overloads
  (any argument count), `Math.Max`/`Math.Min`, and inline-collection `Max()`/`Min()` now
  translate to the native StarRocks `greatest()`/`least()` functions through the relational
  `GenerateGreatest`/`GenerateLeast` visitor hooks, with MySQL NULL semantics (the result is
  NULL when any argument is NULL) encoded in the nullability annotations. The DotRocks 2–4
  argument `EF.Functions.Greatest`/`Least` overloads shipped in 1.3.4 remain as compatible
  sugar.

## [1.3.4] - 2026-07-31

### Added
- EF Core: `EF.Functions.Greatest(...)` and `EF.Functions.Least(...)` (2–4 arguments)
  translate to the native StarRocks `greatest()`/`least()` functions. StarRocks follows
  MySQL NULL semantics — the result is NULL when any argument is NULL, unlike PostgreSQL —
  which is documented on the API and in the README.
- EF Core: composite primary keys are supported on writable entities. `UPDATE`/`DELETE`
  emit one `WHERE` condition per key column, `Find`/`FindAsync` resolve by the full key,
  and migrations create multi-column StarRocks `PRIMARY KEY` tables. The DTR0008 analyzer
  rule (composite primary keys) is retired and no longer reports; the id is reserved and
  will not be reused. The shipped `EfCompositePrimaryKeyAnalyzer` type and
  `CompositePrimaryKeyDiagnosticId` constant remain as obsolete no-ops for binary
  compatibility and will be removed in the next major release.
- EF Core: `UseStarRocks` now validates the connection string at registration and throws a
  descriptive configuration error for a missing/empty or unparsable connection string,
  instead of surfacing an obscure failure on first context use.
- EF Core: verified and pinned test coverage for the EF-standard interpolated raw-SQL
  overloads (`FromSql($"...")`, `Database.SqlQuery<T>($"...")`) with automatic
  parameterization, and for conditional aggregation
  (`Sum(x => cond ? value : null)` → `SUM(CASE WHEN ...)`, `??` → `COALESCE`, `Math.Abs`
  → `abs`) in a single round trip.

### Changed
- Connection strings now fail explicitly on unrecognized keywords
  (`Connection string keyword '...' is not supported.`). Previously an unknown keyword was
  silently ignored and its option fell back to the default — for a misspelled security
  keyword such as `Ssl Mdoe=Required` that failed open by leaving `Ssl Mode` at
  `Preferred` with plaintext fallback. Affects `DotRocksConnection`,
  `DotRocksConnectionStringBuilder`, and `UseStarRocks` registration.

## [1.3.3] - 2026-07-13

### Added
- The ADO.NET synchronous command path (`ExecuteReader`, `ExecuteScalar`, `ExecuteNonQuery`,
  and the `CommandBehavior` overloads) now runs on a native synchronous streaming pipeline
  instead of blocking on the async path through `GetAwaiter().GetResult()`, so large result
  sets stream row-by-row without buffering the whole set or pinning a thread-pool thread on an
  async continuation. `DotRocksDataReader` gains a `DisposeAsync()` override, and
  `ExecuteScalar`/`ExecuteScalarAsync` now request a single row instead of draining the rest of
  the result set.
- Compilable samples for secure (TLS) connections, connection pooling, transactions, and Stream
  Load transactions (`DotRocks.Samples.SecureConnection`, `.ConnectionPooling`, `.Transactions`,
  and `.StreamLoadTransaction`).
- Expanded performance benchmark coverage — analyzer execution, EF Core materialization, protocol
  hot paths, packet framing, and Stream Load — with a broadened performance-budget guard.

### Changed
- Documentation refresh: new connection-string, security, Stream Load, observability, and
  analyzer guides; corrected Stream Load result and transaction guidance; documented the bounded
  metric tags and the canonical EF Core mapping APIs; and clarified analyzer code-fix availability.

### Fixed
- The transitive Roslyn workspace packages are pinned so the analyzer projects can no longer pick
  up a conflicting `Microsoft.CodeAnalysis.*` transitive dependency version.

## [1.3.2] - 2026-07-08

### Fixed
- A binary `TIME` value whose components overflow `TimeSpan` now surfaces as a controlled
  malformed-packet error instead of an uncaught `OverflowException`.
- Dormant connection pools (no idle connections and no outstanding leases) are reaped from the
  process-wide registry so connection strings that vary per request no longer accumulate pool
  objects and their eviction timers; reaping is coordinated with lease admission.

### Security
- Stream Load redirects are now vetted at connect time: the request host is resolved once and the
  socket connects to exactly that vetted address, refusing loopback, link-local (including the
  `169.254.169.254` and IPv6 `fd00:ec2::254` cloud-metadata endpoints), multicast, unspecified,
  and IPv6 unique-local targets unless the configured endpoint is itself loopback. This closes an
  SSRF / credential-forwarding and DNS-rebinding gap and fails closed on resolution failure. The
  configured endpoint host is trusted and exempt; only server-chosen redirect hosts are vetted.
- Unrecognized `Ssl Mode` values — including out-of-range numeric strings and undefined typed-enum
  values set through the connection-string builder — now fail closed instead of silently
  negotiating a plaintext connection.
- Session state mutated by a prepared statement (for example `SET @tenant := ?` or a
  `SELECT ... := ...` user-variable assignment) is no longer reused across leases of a pooled
  connection.
- `Maximum Pool Size` is bounded (rejected at both the connection-string builder setter and parse)
  to resist resource exhaustion from an oversized pool.

## [1.3.1] - 2026-07-06

### Added
- Canonical table-shape fluent names `HasStarRocksRandomDistribution(buckets)` and
  `HasStarRocksSortKey(columns)` so all StarRocks table options share the `HasStarRocks`
  prefix; `DistributedRandomly` and `HasSortKey` remain as forwarding equivalents. The
  design-time scaffolder now emits the canonical names.
- `DotRocksJson` values can now be bound as command parameters on both the text protocol
  (escaped string literal) and the binary prepared protocol; previously both paths threw
  `NotSupportedException`.

### Changed
- Table-shape annotations (key model, distribution, sort key, buckets, replication,
  properties) are now driven by one internal registry shared by the relational annotation
  provider, the model validator, the migrations SQL generator, and the design-time code
  generator, with a completeness test so a new option cannot be wired partially.
- Model validation reports invalid table-shape configuration earlier and more precisely:
  sort-key columns must map to store columns, wrong-typed annotation values are rejected
  at model finalization instead of during SQL generation, and shared-table conflict
  checks now cover random distribution, sort keys, and table properties.
- Extensive internal consolidation with no behavior change: the driver's four command
  paths share one packet-exchange and exception-translation helper, text and binary
  result-set parsing share one reader, the parameter-binder lexer twins are unified, and
  the protocol test suites share one fake server and packet factory.

### Fixed
- Migrations generated by the model differ no longer silently drop `DISTRIBUTED BY
  RANDOM`, `ORDER BY` sort keys, and custom `PROPERTIES`: the relational annotation
  provider now forwards those annotations into diffed `CreateTableOperation`s.
- Equivalent table `PROPERTIES` dictionaries on entities sharing a table no longer report
  a false conflict (comparison is now by content, not reference).
- `ObjectDisposedException` during statement prepare or prepared execution is wrapped as
  a transient `DotRocksException` like every other command path instead of escaping raw.
- Integration readiness probes (local `just starrocks-up` and CI) now verify a backend
  accepts `CREATE TABLE` before tests start; previously suites could launch while only
  the StarRocks frontend was up and fail spuriously.

## [1.3.0] - 2026-07-02

### Added
- `DotRocksStreamLoadException.ResponseBody` carries the raw server response body on
  Stream Load HTTP failures so diagnostic detail (auth, label, format errors) is no longer
  discarded; the exception message itself still never embeds untrusted server text.

### Changed
- Refreshed README, DocFX docs, security notes, and project agent context for the 1.2.0
  release state; tightened wording around tested support boundaries and local validation.
- `DotRocks.Analyzers.CodeFixes` now declares a NuGet dependency on `DotRocks.Analyzers`,
  so installing the code-fix package alone no longer produces a code-fix assembly whose
  analyzer dependency cannot load in the IDE.
- The release workflow validates the tag format and matching `CHANGELOG.md` section, and
  gates publishing on the full StarRocks integration matrix; all GitHub Actions are pinned
  to commit SHAs.

### Fixed
- Commands whose payload spans multiple protocol packets (≥ 16 MiB) no longer fail with an
  out-of-order sequence error: the response reader now continues from the writer's final
  sequence id.
- Disposing a partially-read data reader drains the remaining result set and leaves the
  connection open and usable instead of closing the logical connection.
  `CommandBehavior.SingleRow` and `CommandBehavior.SchemaOnly` are now honored.
- A benign server error (for example a SQL typo) no longer closes the logical connection
  when the session has run `SET`/`USE` or the physical connection has exceeded its
  lifetime; only genuinely broken connections are closed.
- A server error arriving mid result set on the prepared (binary) protocol now surfaces
  the real server error code and message instead of a malformed-protocol failure, and the
  connection stays usable.
- `TIME` values outside `TimeSpan.Parse` range (up to MySQL's `838:59:59`, including
  negative and fractional values) parse correctly on the text protocol.
- `GetString` and `GetFieldValue<string>` on binary columns throw `InvalidCastException`
  instead of silently returning `"System.Byte[]"`.
- Binary-protocol `YEAR` values box as `int`, matching `GetFieldType` and the text path.
- Cancellation during `COM_STMT_CLOSE` marks the physical connection broken so a
  desynchronized connection can never return to the pool.
- A pool-creation race no longer leaks the losing pool's idle-eviction timer.
- `DotRocksDataSource.ConnectionString` returns the redacted connection string (password
  omitted), matching `DotRocksConnection.ConnectionString`; created connections still
  authenticate with the original credentials.
- Connection-string values containing `;`, quotes, or `=` are serialized with proper
  `DbConnectionStringBuilder` quoting instead of a backslash escape the parser does not
  understand, closing an option-injection hole on the serialize→reparse round-trip.
- EF Core string literals (string/varchar/json, and the defensive string branch of the
  `Guid` mapping) and migration `PROPERTIES` keys/values escape backslashes and control
  characters the same way the driver's literal formatter does; single quotes in table
  properties are now escaped rather than rejected.
- A plain `decimal` property with precision beyond the native range maps through a
  `decimal` ↔ `DotRocksDecimal` value converter instead of a converter-less mapping with a
  mismatched CLR type.
- Scaffolding round-trips no longer lose table shape: the design-time annotation code
  generator emits `DistributedRandomly(...)` for random distribution (previously a broken
  zero-column hash-distribution call), `HasSortKey(...)`, and `HasStarRocksProperty(...)`.
- The multi-row `SaveChanges` analyzer (DTR0007) only pairs a range operation with a
  `SaveChanges` call on the same `DbContext` instance and ignores mutually exclusive
  branches, removing false positives.

## [1.2.0] - 2026-06-25

### Added
- Advanced StarRocks table-model fluent APIs for EF Core migrations: `DistributedRandomly(buckets)`
  (`DISTRIBUTED BY RANDOM`), `HasSortKey(columns)` (`ORDER BY`), and
  `HasStarRocksProperty(name, value)` (additional `PROPERTIES`, validated against quote injection).
  Verified end to end against StarRocks 4.0.7.
- EF Core query translators for `DateTime`/`DateOnly` members (`Year`, `Month`, `Day`, `Hour`,
  `Minute`, `Second`, `DayOfYear`, `Date`), `Add…` methods (`days_add` / `months_add` / …), and
  `Math` methods (`Abs`, `Ceiling`, `Floor`, `Round`, `Round(x, n)`, `Pow`, `Sqrt`, `Exp`, `Log`,
  `Sign`), mapped to StarRocks functions and verified against StarRocks 4.0.7.
- Observability timing histograms on the `DotRocks.Data` meter:
  `dotrocks.connection.open.duration` (pool acquisition + physical open) and
  `dotrocks.transaction.duration` (begin to commit/rollback), each tagged with a bounded `outcome`.
- `DbConnection.GetSchema()` metadata collections over StarRocks `INFORMATION_SCHEMA`:
  `MetaDataCollections`, `Databases`, `Tables`, `Views`, and `Columns`, with restriction filtering.
  Verified against StarRocks 4.0.7.
- Server-side prepared statements via `DotRocksParameterMode.ServerPrepared`: the binary
  `COM_STMT_PREPARE` / `COM_STMT_EXECUTE` / `COM_STMT_CLOSE` protocol with binary parameter encoding
  and binary result-row decoding, verified end to end against StarRocks 4.0.7. Use positional `?`
  placeholders and add parameters in order. Unsupported parameter value types fail with
  `DotRocksUnsupportedFeatureException`. Prepared statements are cached and reused per physical
  connection. StarRocks 4.0.7 allows only `SELECT` in the prepared protocol — prepared writes are
  rejected by the server, so use the text protocol (`Auto`) for parameterized DML.
- `DotRocksJson`, an immutable lossless wrapper for StarRocks `JSON` values, readable via
  `reader.GetFieldValue<DotRocksJson>(ordinal)`. It preserves the server's exact bytes and offers
  `Parse()` for a caller-owned `JsonDocument`. Verified against StarRocks 4.0.7, which returns JSON
  over the text protocol typed as `STRING` (so JSON is opt-in typed access, not an automatic map).
  For the cases exercised by the integration suite (including nested values, `null` elements,
  escaped strings, and decimal/date values), `ARRAY` / `MAP` / `STRUCT` are returned as
  JSON-formatted text (typed `VAR_STRING`) and read losslessly through `DotRocksJson`. The
  aggregate-state types `BITMAP` / `HLL` / `PERCENTILE` are opaque (a direct select yields `NULL`);
  read them through StarRocks accessor functions such as `bitmap_to_string(...)`.
- A protocol fuzz harness with a regression corpus that feeds random and adversarial bytes to the
  handshake, OK/error packet, and length-encoded readers, asserting they fail only with a
  controlled `MalformedPacketException`/`DotRocksException` and never an uncontrolled crash.
- A parameter-tokenizer fuzz harness that feeds adversarial command text (unbalanced quotes,
  comments, dangling placeholders) and diverse CLR values to the binder and literal formatter,
  asserting controlled failures only and that placeholders inside string literals are never
  substituted.
- Stream Load metrics on the `DotRocks.Data` meter: `dotrocks.stream_load.duration` (ms),
  `dotrocks.stream_load.rows_loaded`, `dotrocks.stream_load.rows_filtered`, and
  `dotrocks.stream_load.bytes`, tagged only with a bounded `outcome`.
- Stream Load partition targeting (`DotRocksStreamLoadOptions.Partitions`) and on-the-fly gzip
  payload compression (`DotRocksStreamLoadOptions.Compression = DotRocksStreamLoadCompression.Gzip`),
  verified against StarRocks 4.0.7. Compression is streamed (the upload is never buffered in memory)
  and reported via the `gzip` load format; it applies to CSV payloads only.
- Compilable samples for the ADO.NET surface, dependency-injection wiring, Dapper, and Stream
  Load (`DotRocks.Samples.AdoNet`, `.DependencyInjection`, `.Dapper`, `.StreamLoad`). DotRocks.Data
  stays dependency-free, so the DI sample shows idiomatic `DbDataSource` registration in user code.
- `StarRocksServerVersion` and `DotRocksDbContextOptionsBuilder.ServerVersion(...)` to pin the
  target StarRocks version when configuring the EF Core provider, plus an opt-in
  `StarRocksServerVersion.DetectAsync(connectionString)` that reads `SELECT current_version()`.
  Building `DbContextOptions` never contacts the server. `StarRocksServerVersion` implements
  `IComparable<StarRocksServerVersion>` and comparison operators for version gating such as
  `version >= new StarRocksServerVersion(3, 5)`.
- EF Core query translation now emits SQL for explicit relational joins (`Join`,
  `GroupJoin`/`SelectMany`+`DefaultIfEmpty`, cross joins) and for `GroupBy` with `HAVING`
  predicates and aggregate functions, instead of throwing `NotSupportedException`.
  Navigation-based joins and `Include` remain unsupported because relationships are still
  rejected at model validation.
- Four driver-usage analyzers: `DTR0009` (interpolated/concatenated SQL in
  `DotRocksCommand.CommandText`), `DTR0010` (async DotRocks call missing an available
  `CancellationToken`), `DTR0011` (blocking on a DotRocks async call), and `DTR0012`
  (hard-coded password in a connection string). Disposal is intentionally left to the
  built-in `CA2000` analyzer.
- Public API surface tracking via `Microsoft.CodeAnalysis.PublicApiAnalyzers` with
  `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` baselines for `DotRocks.Data`,
  `DotRocks.EntityFrameworkCore`, and `DotRocks.EntityFrameworkCore.Design`, so accidental
  breaking changes to the public API now fail the build. Package validation is enabled on
  the shipping packages.

### Changed
- `DotRocksDbContextOptionsBuilder` is now a relational options builder bound to the
  `DbContextOptionsBuilder`; its previously non-functional public parameterless constructor was
  removed. Application code configures it only through the `UseStarRocks(...)` options action.
- Reduced per-row and per-call allocations on hot paths with no change to observable behavior:
  result-value decoding now parses directly from UTF-8 spans, the wire-protocol integer reader and
  writer use `BinaryPrimitives`, SQL literal escaping fast-paths through `SearchValues`, and the
  EF Core function-lookup tables are `FrozenDictionary`.

### Fixed
- `Math.Round(value, MidpointRounding)` is no longer translated to SQL with the rounding mode
  mistaken for a digit count; it now falls back to client evaluation like other untranslatable
  calls.

### Security
- The ADO.NET `DbConnection.ConnectionString` getter no longer returns the password (the
  `PersistSecurityInfo=false` convention), so logging or echoing it cannot leak the secret.
- Binary prepared-statement temporal decoders raise a controlled `MalformedPacketException` on
  out-of-range `DATETIME` / `TIME` components instead of an uncontrolled exception.

## [1.1.0] - 2026-06-24

### Changed
- `Ssl Mode` now defaults to the new `Preferred` value (opportunistic TLS: upgrade when the
  server advertises support, otherwise plaintext) instead of `Disabled`. Set `Ssl Mode=Required`
  to fail when TLS cannot be negotiated, or `Ssl Mode=Disabled` to restore the previous default.

### Security
- Redacted the password and cleartext connection string from `DotRocksConnectionOptions`'s
  `ToString()` output.
- Capped the server-advertised result-set column count before pre-allocation to prevent an
  out-of-memory denial of service from a hostile or corrupt server.
- Stripped control characters from server-provided error text surfaced through
  `DotRocksException.Message` to prevent log forging.
- Bounded the server-provided SQLSTATE used in telemetry `error.type` /
  `db.response.status_code` to a well-formed value.

## [1.0.1] - 2026-06-23

### Added
- Generic `UseStarRocks<TContext>` overloads for `DbContextOptionsBuilder<TContext>` so the
  fluent options chain keeps its context type into `.Options`.
- `DTR0008` analyzer that flags unsupported composite primary keys on EF Core entities, with
  an `.editorconfig` escalation to an error.
- EF Core entity mapping guide covering writable single-column-key entities and read-only
  `HasNoKey()` entities.

### Changed
- Package `Authors`, `Company`, and `Copyright` metadata set to the project owner.
- Package and documentation URLs point at the canonical repository.

## [1.0.0] - 2026-06-21

### Added
- ADO.NET provider (`DotRocks.Data`) with a native StarRocks/MySQL wire-protocol
  implementation: connections, commands, parameters, transactions, data reader, connection
  pooling, data source, and provider factory.
- Streaming and buffered text result sets, high-precision `DotRocksDecimal`, `Int128`
  (LARGEINT), and binary/`byte[]` value support.
- HTTP Stream Load client with transactions, in-doubt handling, and idempotency labels.
- TLS (`Ssl Mode=Required`) with configurable certificate revocation checking.
- Entity Framework Core 10 provider (`DotRocks.EntityFrameworkCore`) with a verified LINQ
  subset, constrained writes, minimal migrations, and a design-time package.
- Roslyn analyzer suite (`DotRocks.Analyzers`) with code fixes.
- OpenTelemetry-compatible tracing and metrics via `DotRocksTelemetry`.

### Security
- Connection-string and credential redaction across exceptions and diagnostics.
- Stream Load refuses to forward credentials over a downgraded (HTTPS→HTTP) redirect.
- NuGet vulnerability auditing and CodeQL analysis in CI.

[Unreleased]: https://github.com/kidoz/dotrocks/compare/v1.5.0...HEAD
[1.5.0]: https://github.com/kidoz/dotrocks/compare/v1.4.2...v1.5.0
[1.4.2]: https://github.com/kidoz/dotrocks/compare/v1.4.1...v1.4.2
[1.4.1]: https://github.com/kidoz/dotrocks/compare/v1.4.0...v1.4.1
[1.4.0]: https://github.com/kidoz/dotrocks/compare/v1.3.5...v1.4.0
[1.3.5]: https://github.com/kidoz/dotrocks/compare/v1.3.4...v1.3.5
[1.3.4]: https://github.com/kidoz/dotrocks/compare/v1.3.3...v1.3.4
[1.3.3]: https://github.com/kidoz/dotrocks/compare/v1.3.2...v1.3.3
[1.3.2]: https://github.com/kidoz/dotrocks/compare/v1.3.1...v1.3.2
[1.3.1]: https://github.com/kidoz/dotrocks/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/kidoz/dotrocks/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/kidoz/dotrocks/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/kidoz/dotrocks/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/kidoz/dotrocks/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/kidoz/dotrocks/releases/tag/v1.0.0
