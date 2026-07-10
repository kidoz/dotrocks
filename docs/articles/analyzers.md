# Analyzers

DotRocks ships a Roslyn analyzer suite (`DotRocks.Analyzers`) that catches incorrect,
insecure, or unsupported usage at build time. The analyzers add **no runtime dependency**
to consumer output — they are build-time only.

> The authoritative descriptor definitions live in
> [`DotRocksDiagnosticDescriptors.cs`](https://github.com/kidoz/dotrocks/blob/main/src/DotRocks.Analyzers/Infrastructure/DotRocksDiagnosticDescriptors.cs).
> This article mirrors that source.

## Installation

Reference the analyzers package with `PrivateAssets="all"` so it does not flow to
consumers of your project:

```xml
<ItemGroup>
  <PackageReference Include="DotRocks.Analyzers" Version="1.3.2">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

For IDE squiggle-to-fix ergonomics, optionally add `DotRocks.Analyzers.CodeFixes` the same
way — it carries code-fix providers for the diagnostics where the correction is mechanical
(currently DTR0001 and DTR0002).

## Diagnostic catalog

All diagnostics default to **Warning** severity and are enabled by default.

### Security

| ID | Trigger | Recommendation |
|---|---|---|
| **DTR0001** | A connection string uses an HTTP Stream Load endpoint with credentials. | Use HTTPS for the `Stream Load Endpoint` so Basic credentials are not sent in cleartext. See [Security](security.md). |
| **DTR0009** | Interpolated or concatenated SQL is assigned to `DotRocksCommand.CommandText`. | Use parameter placeholders (e.g. `@id`) with `DotRocksParameter` values. This is an SQL-injection signal. |
| **DTR0012** | A DotRocks connection string contains a literal password. | Load the password from configuration, environment, or a secret store. |

### Usage — EF Core

| ID | Trigger | Recommendation |
|---|---|---|
| **DTR0002** | A writable entity key property lacks `ValueGeneratedNever()`. | DotRocks SaveChanges supports explicit values only; configure keys with `ValueGeneratedNever()`. |
| **DTR0003** | An entity maps a `binary`/`varbinary` column. | Binary mapping is unsupported until the EF read/write surface is verified end to end. |
| **DTR0005** | Code calls `EnsureCreated` / `EnsureDeleted`. | Use migrations for conservative StarRocks DDL; these database-creator APIs are unsupported. |
| **DTR0006** | Code calls `ExecuteUpdate` / `ExecuteDelete`. | DotRocks does not translate bulk LINQ DML. Use tracked single-row `SaveChanges` or raw SQL with parameters. |
| **DTR0007** | A range change (`Add`/`Update`/`Remove` on multiple entities) is followed by `SaveChanges`. | StarRocks rejects a second DML against a table already written in the same transaction; write one row per `SaveChanges`. |
| **DTR0008** | An entity is configured with a composite primary key. | DotRocks requires a single-column PK for writable entities; use `HasNoKey()` for read-only entities. |

### Usage — driver

| ID | Trigger | Recommendation |
|---|---|---|
| **DTR0004** | A transaction variable is committed or rolled back more than once. | DotRocks transactions (SQL and Stream Load) are single-use after completion. |
| **DTR0010** | An async DotRocks call does not pass the `CancellationToken` available in scope. | Pass the token so the call observes cancellation. |
| **DTR0011** | Code blocks on an async DotRocks call (`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`). | `await` the call instead — blocking can exhaust the thread pool and deadlock. |

## Escalating severity

Diagnostics are warnings by default. To fail the build on a specific pattern, escalate it
to `error` in `.editorconfig`:

```ini
[*.cs]
# Fail the build on SQL-injection-shaped CommandText and composite keys.
dotnet_diagnostic.DTR0009.severity = error
dotnet_diagnostic.DTR0008.severity = error
```

This is the recommended baseline for DTR0009 (SQL injection) and DTR0012 (literal
password). Project policy decides the rest.

## See also

- [EF Core entity mapping](ef-core-entity-mapping.md) — the model-validation rules behind DTR0002/DTR0003/DTR0008
- [Security](security.md) — the transport-security context for DTR0001/DTR0012
- [Connection strings](connection-strings.md) — credential redaction and the typed builder
