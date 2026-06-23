# Changelog

All notable changes to DotRocks are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The package
version is derived from the release tag at publish time.

## [Unreleased]

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

[Unreleased]: https://github.com/kidoz/dotrocks/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/kidoz/dotrocks/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/kidoz/dotrocks/releases/tag/v1.0.0
