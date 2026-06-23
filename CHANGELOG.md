# Changelog

All notable changes to DotRocks are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The package
version is derived from the release tag at publish time.

## [Unreleased]

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

[Unreleased]: https://github.com/kidoz/dotrocks/commits/main
