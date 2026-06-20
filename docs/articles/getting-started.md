# Getting started

## Requirements

- .NET 10 SDK
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

DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
    .UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root")
    .Options;

await using var context = new AppDbContext(options);
```

Write one row per `SaveChanges`, or use Stream Load for bulk ingestion. See the repository
README for the full supported/unsupported EF Core surface.

## Observability

DotRocks emits OpenTelemetry-compatible tracing and metrics under the `DotRocks.Data`
`ActivitySource` and `Meter` names.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(DotRocksTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(DotRocksTelemetry.MeterName));
```
