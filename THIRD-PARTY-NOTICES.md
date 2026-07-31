# Third-Party Notices

DotRocks uses centrally pinned NuGet packages. Runtime packages should carry only their
declared runtime dependencies; analyzer packages are analyzer-only and suppress runtime
dependencies.

| Package | Purpose |
| --- | --- |
| `Apache.Arrow` | Runtime columnar arrays and record batches for the Flight SQL transport. |
| `Apache.Arrow.Flight` | Runtime Arrow Flight RPC client for result streaming. |
| `Apache.Arrow.Flight.AspNetCore` | Test-only in-process Arrow Flight server hosting. |
| `Apache.Arrow.Flight.Sql` | Runtime Flight SQL command and metadata handling. |
| `BenchmarkDotNet` | Benchmark-only performance measurement harness. |
| `Dapper` | Test-only Dapper compatibility coverage. |
| `Microsoft.CodeAnalysis.Analyzers` | Analyzer project build-time Roslyn analyzer rules. |
| `Microsoft.CodeAnalysis.CSharp` | Analyzer implementation and analyzer unit tests. |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | Code-fix implementation and tests. |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | Build-time public API surface tracking for shipping packages. |
| `Microsoft.CodeAnalysis.Workspaces.Common` | Transitive Roslyn workspace pin for EF Core design-time tooling. |
| `Microsoft.CodeAnalysis.Workspaces.MSBuild` | Transitive Roslyn workspace pin for EF Core design-time tooling. |
| `Microsoft.Extensions.DependencyInjection` | Dependency-injection sample wiring. |
| `Microsoft.EntityFrameworkCore` | EF Core provider implementation and tests. |
| `Microsoft.EntityFrameworkCore.Design` | EF Core design-time migrations services. |
| `Microsoft.EntityFrameworkCore.Relational` | EF Core relational provider services. |
| `Microsoft.NET.Test.Sdk` | Test execution infrastructure. |
| `Grpc.Core.Api` | Runtime gRPC call metadata used by the Flight SQL transport. |
| `Grpc.Net.Client` | Runtime HTTP/2 channel implementation used by the Flight SQL transport. |
| `coverlet.collector` | Test coverage collection support. |
| `xunit.runner.visualstudio` | xUnit test runner integration. |
| `xunit.v3` | Unit and integration test framework. |
