# DotRocks for StarRocks

DotRocks is a native .NET driver, Entity Framework Core provider, and Roslyn analyzer suite
built specifically for [StarRocks](https://www.starrocks.io/). It implements its own managed
StarRocks client protocol and takes no dependency on any MySQL driver.

The latest tagged release is 1.3.0. The `main` branch is post-1.3.0; use the repository
README and changelog for unreleased behavior.

## Packages

| Package | Description |
|---|---|
| `DotRocks.Data` | Native ADO.NET provider with its own managed StarRocks protocol implementation. |
| `DotRocks.EntityFrameworkCore` | EF Core relational provider built on `DotRocks.Data`. |
| `DotRocks.EntityFrameworkCore.Design` | Design-time EF Core services for migrations. |
| `DotRocks.Analyzers` | Roslyn analyzers for correct, secure DotRocks usage. |
| `DotRocks.Analyzers.CodeFixes` | Optional IDE code fixes for DotRocks analyzer diagnostics. |

## Documentation

- [Getting started](articles/getting-started.md)
- [EF Core entity mapping](articles/ef-core-entity-mapping.md)
- [StarRocks 3.x driver developer notes](starrocks-3x-driver-developer-notes.md)
- [StarRocks 4.x driver developer notes](starrocks-4x-driver-developer-notes.md)
- [API reference](xref:DotRocks.Data.DotRocksConnection)

## Compatibility

The live integration matrix runs against StarRocks 3.5.5 and 4.0.7. Version-specific
behavior is gated from `SELECT current_version()` or the `Server Compatibility Level`
connection-string override.

Build this site locally with:

```bash
dotnet tool restore
dotnet docfx docs/docfx.json
```

See the repository [README](https://github.com/kidoz/dotrocks) for the full supported
ADO.NET, EF Core, Stream Load, analyzer, and observability surface.
