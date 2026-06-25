# Changelog

All notable changes to DotRocks are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The package
version is derived from the release tag at publish time.

## [Unreleased]

### Changed
- `DotRocksDbContextOptionsBuilder` is now a relational options builder bound to the
  `DbContextOptionsBuilder`; its previously non-functional public parameterless constructor was
  removed. Application code configures it only through the `UseStarRocks(...)` options action.

### Added
- Compilable samples for the ADO.NET surface, dependency-injection wiring, Dapper, and Stream
  Load (`DotRocks.Samples.AdoNet`, `.DependencyInjection`, `.Dapper`, `.StreamLoad`). DotRocks.Data
  stays dependency-free, so the DI sample shows idiomatic `DbDataSource` registration in user code.
- `StarRocksServerVersion` and `DotRocksDbContextOptionsBuilder.ServerVersion(...)` to pin the
  target StarRocks version when configuring the EF Core provider, plus an opt-in
  `StarRocksServerVersion.DetectAsync(connectionString)` that reads `SELECT current_version()`.
  Building `DbContextOptions` never contacts the server.
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

[Unreleased]: https://github.com/kidoz/dotrocks/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/kidoz/dotrocks/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/kidoz/dotrocks/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/kidoz/dotrocks/releases/tag/v1.0.0
