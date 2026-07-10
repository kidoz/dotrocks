# Getting started

## Requirements

- .NET 10 SDK, pinned by `global.json`
- A reachable StarRocks FE (MySQL-protocol query port, default 9030)

Install the packages you use:

```xml
<PackageReference Include="DotRocks.Data" Version="1.3.2" />
<PackageReference Include="DotRocks.EntityFrameworkCore" Version="1.3.2" />
<PackageReference Include="DotRocks.EntityFrameworkCore.Design" Version="1.3.2" PrivateAssets="all" />
<PackageReference Include="DotRocks.Analyzers" Version="1.3.2" PrivateAssets="all" />
```

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

Write one row per `SaveChanges`, or use Stream Load for bulk ingestion. See
[EF Core entity mapping](ef-core-entity-mapping.md) for model validation and migration
table-shape rules.

## Stream Load

```csharp
using DotRocks.Data.Loading;

using var client = new DotRocksStreamLoadClient(
    "Server=starrocks.example.com;Port=9030;User ID=loader;Password=secret;Stream Load Endpoint=https://starrocks.example.com:8030"
);

await using Stream csv = File.OpenRead("events.csv");
DotRocksStreamLoadResult result = await client.LoadCsvAsync(
    "warehouse",
    "events",
    csv,
    new DotRocksStreamLoadOptions
    {
        Label = "events_20260626",
        Columns = "id,name,created_at",
        RowDelimiter = "\\n",
    }
);
```

HTTP Stream Load endpoints are rejected unless you use HTTPS or explicitly set
`Allow Insecure Stream Load=true` for a trusted local test server.

## Observability

DotRocks emits OpenTelemetry-compatible tracing and metrics under the `DotRocks.Data`
`ActivitySource` and `Meter` names.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(DotRocksTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(DotRocksTelemetry.MeterName));
```

## Local validation

```bash
dotnet tool restore
dotnet restore --locked-mode
dotnet csharpier check .
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
```

Live tests require StarRocks and run with `DOTROCKS_RUN_INTEGRATION=1`; `just integration-test`
starts from an already-running server configured by the local Docker recipe.
