# Getting started

## Requirements

- .NET 10 SDK, pinned by `global.json`
- A reachable StarRocks FE (MySQL-protocol query port, default 9030)

## ADO.NET

```csharp
await using var connection = new DotRocksConnection(
    "Server=127.0.0.1;Port=9030;User ID=root"
);
await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = "SELECT 1";
object? value = await command.ExecuteScalarAsync();
```

For a non-local server, enable TLS with `Ssl Mode=Required`.

## Entity Framework Core

```csharp
using Microsoft.EntityFrameworkCore;
using DotRocks.EntityFrameworkCore.Infrastructure;

DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
    .UseStarRocks(
        "Server=127.0.0.1;Port=9030;User ID=root",
        starRocks => starRocks.ServerVersion(new StarRocksServerVersion(4, 0, 7)))
    .Options;

await using var context = new AppDbContext(options);
```

Pin the server version in provider options. To discover it once at startup, call
`StarRocksServerVersion.DetectAsync(connectionString)` and cache the result.

Write one row per `SaveChanges`, or use Stream Load for bulk ingestion. The repository
README lists the supported and unsupported EF Core surface.

## Observability

DotRocks emits OpenTelemetry-compatible tracing and metrics under the `DotRocks.Data`
`ActivitySource` and `Meter` names.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(DotRocksTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(DotRocksTelemetry.MeterName));
```
