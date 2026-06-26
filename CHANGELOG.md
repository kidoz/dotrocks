# Changelog

All notable changes to DotRocks are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The package
version is derived from the release tag at publish time.

## [Unreleased]

### Changed
- Refreshed README, DocFX docs, security notes, and project agent context for the 1.2.0
  release state; tightened wording around tested support boundaries and local validation.

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

[Unreleased]: https://github.com/kidoz/dotrocks/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/kidoz/dotrocks/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/kidoz/dotrocks/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/kidoz/dotrocks/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/kidoz/dotrocks/releases/tag/v1.0.0
