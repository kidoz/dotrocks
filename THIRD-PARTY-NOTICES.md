# Third-Party Notices

DotRocks uses centrally pinned NuGet packages. Runtime packages should carry only their
declared runtime dependencies; analyzer packages are analyzer-only and suppress runtime
dependencies.

| Package | Purpose |
| --- | --- |
| `BenchmarkDotNet` | Benchmark-only performance measurement harness. |
| `Dapper` | Test-only Dapper compatibility coverage. |
| `Microsoft.CodeAnalysis.Analyzers` | Analyzer project build-time Roslyn analyzer rules. |
| `Microsoft.CodeAnalysis.CSharp` | Analyzer implementation and analyzer unit tests. |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | Code-fix implementation and tests. |
| `Microsoft.EntityFrameworkCore` | EF Core provider implementation and tests. |
| `Microsoft.EntityFrameworkCore.Design` | EF Core design-time migrations services. |
| `Microsoft.EntityFrameworkCore.Relational` | EF Core relational provider services. |
| `Microsoft.NET.Test.Sdk` | Test execution infrastructure. |
| `coverlet.collector` | Test coverage collection support. |
| `xunit.runner.visualstudio` | xUnit test runner integration. |
| `xunit.v3` | Unit and integration test framework. |
