# DotRocks for StarRocks

DotRocks is a native .NET driver, Entity Framework Core provider, and Roslyn analyzer suite
built specifically for [StarRocks](https://www.starrocks.io/). It implements its own managed
StarRocks client protocol and takes no dependency on any MySQL driver.

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
- [StarRocks 3.x driver developer notes](starrocks-3x-driver-developer-notes.md)
- [StarRocks 4.x driver developer notes](starrocks-4x-driver-developer-notes.md)
- [API reference](xref:DotRocks.Data.DotRocksConnection)

See the repository [README](https://github.com/dotrocks/dotrocks) for the full feature matrix,
supported EF Core surface, StarRocks compatibility notes, and security posture.
