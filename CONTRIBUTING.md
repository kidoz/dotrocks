# Contributing to DotRocks

Thanks for your interest in DotRocks — a native .NET driver, EF Core provider, and Roslyn
analyzer suite for StarRocks.

## Prerequisites

- .NET 10 SDK (pinned via `global.json`).
- All runtime products target `net10.0`; the analyzer packages target `netstandard2.0` as a
  documented compiler/IDE-host exception. There is no other multi-targeting.

## Build, format, and test

```bash
dotnet tool restore
dotnet csharpier check .                                  # formatting gate (CI runs this)
dotnet restore DotRocks.slnx --locked-mode
dotnet build DotRocks.slnx --configuration Release
dotnet test tests/DotRocks.Protocol.Tests                 # server-free unit tests
dotnet test tests/DotRocks.EntityFrameworkCore.Tests
dotnet test tests/DotRocks.Analyzers.Tests
dotnet test tests/DotRocks.Benchmarks.Tests
dotnet test tests/DotRocks.PackageTests
```

Integration tests require a live StarRocks and run via `just integration-test` (or the
`integration.yml` workflow) with `DOTROCKS_RUN_INTEGRATION=1`. Without it they are skipped,
not silently passed.

## Conventions

- **Formatting:** CSharpier is the only formatter; CI fails on unformatted files.
- **Analysis:** warnings are errors (`TreatWarningsAsErrors`, `AnalysisMode=AllEnabledByDefault`).
  Suppress narrowly with a justification, never project-wide.
- **Public API:** changes to public surface must update the baselines in
  `tests/DotRocks.PackageTests/PublicApi/` (run a test with `DOTROCKS_UPDATE_PUBLIC_API=1`).
- **Commits:** one feature per commit, short imperative subject, no trailing period, no type
  prefixes (e.g. `Add connection pooling`). Do not commit `.gitignore` as part of a feature.
- **Tests:** new behavior ships with tests. "Nothing is described as working unless it is
  built and tested."
- **Benchmarks:** hot-path changes should run `dotnet run --project benchmarks/DotRocks.Benchmarks
  --configuration Release -- --filter '*'`; the benchmark executable enforces configured
  mean-time and allocation budgets.

## Pull requests

Keep PRs focused. Ensure the build, formatting check, and unit suites pass. Describe the
change and its testing in the PR template.
