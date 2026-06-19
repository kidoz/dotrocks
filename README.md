# DotRocks

[![Language](https://img.shields.io/badge/language-C%23-512BD4)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET SDK](https://img.shields.io/badge/.NET%20SDK-10.0.301-512BD4)](global.json)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**DotRocks for StarRocks** — a native .NET driver, Entity Framework Core provider, and
Roslyn analyzer suite built specifically for [StarRocks](https://www.starrocks.io/).

> DotRocks is an independent open-source project. It is not an official StarRocks
> project and does not imply StarRocks endorsement.

## Packages

| Package | Description |
|---|---|
| `DotRocks.Data` | Native ADO.NET provider with its own managed StarRocks protocol implementation. |
| `DotRocks.EntityFrameworkCore` | EF Core relational provider built on `DotRocks.Data`. |
| `DotRocks.EntityFrameworkCore.Design` | Design-time EF Core services for migrations. |
| `DotRocks.Analyzers` | Roslyn analyzers (and code fixes) for correct, secure DotRocks usage. |

## Status

Early development. Nothing here is described as working unless it is built and tested.

DotRocks implements **its own** managed StarRocks client protocol. It takes no runtime
dependency on MySqlConnector, Oracle MySQL Connector/NET, Pomelo, or any other MySQL
driver, and it is not a general-purpose MySQL driver.

## Requirements

- .NET 10 SDK (pinned via `global.json`)
- C# 14

## Build and test

Common tasks are exposed via [`just`](https://github.com/casey/just) (see `justfile`):

```bash
just            # list recipes
just ci         # restore, format check, build, test
just build
just test
just pack
```

The equivalent raw commands:

```bash
dotnet tool restore
dotnet restore --locked-mode
dotnet csharpier check .
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet pack --configuration Release --no-build
```

Integration tests run against a real StarRocks server and are documented separately; the
commands above cover the server-free unit and protocol tests.

### StarRocks compatibility harness (Docker)

```bash
just starrocks-up      # start a pinned StarRocks container, wait until query-ready
just harness           # probe the handshake and write a sanitized report
just starrocks-down    # stop and clean up
```

The harness reads the initial handshake through the DotRocks framing and handshake layers
and writes a sanitized JSON report (authentication challenge bytes are never recorded).

## License

Licensed under the [MIT License](LICENSE).
