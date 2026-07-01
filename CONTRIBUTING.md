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
dotnet restore --locked-mode
dotnet csharpier check .
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet pack --configuration Release --no-build
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
  prefixes (e.g. `Add connection pooling`). Keep unrelated housekeeping (such as `.gitignore`
  edits) out of feature commits.
- **Tests:** new behavior ships with tests. "Nothing is described as working unless it is
  built and tested."
- **Benchmarks:** hot-path changes should run `just bench`; the benchmark executable enforces
  configured mean-time and allocation budgets.

## Pull requests

Keep PRs focused. Ensure the build, formatting check, and unit suites pass. Describe the
change and its testing in the PR template.

## Releasing

There is no version number in the repository: the package version is derived from the git
tag at publish time. Consequently, `dotnet pack` locally and the `packaging.yml` CI
artifacts always produce `1.0.0` packages — that is intentional; only `release.yml` builds
real versions.

To cut release `X.Y.Z`:

1. **Update the changelog.** Move the `[Unreleased]` notes in `CHANGELOG.md` into a new
   `## [X.Y.Z] - YYYY-MM-DD` section and update the compare links at the bottom. The release
   workflow fails if this section is missing.
2. **Verify CI on the release commit.** All workflows — including the full StarRocks
   integration matrix (`integration.yml`) — must be green on the commit you are about to tag.
3. **Tag and publish.** Create tag `vX.Y.Z` (strict semver; an optional `-prerelease` suffix
   such as `v1.3.0-rc.1` is allowed) on that commit and publish a GitHub release for it.
4. **What the workflow does.** `release.yml` then:
   - validates the tag format and the matching `CHANGELOG.md` section (fails fast otherwise);
   - re-runs the full StarRocks integration matrix at the tagged commit;
   - runs the standard gate (locked restore, CSharpier check, build, server-free test suites)
     with `-p:Version=X.Y.Z`, packs, attests provenance, and uploads the artifacts;
   - waits for the `nuget` environment approval, then publishes to nuget.org via Trusted
     Publishing.
5. **No recall.** nuget.org versions are immutable: a published version can be unlisted but
   never re-published. If a release is broken, ship a new patch version; do not delete and
   re-create the tag.

A `workflow_dispatch` run of `release.yml` is a dry run: it builds `0.0.0-manual` packages
and uploads them as workflow artifacts without publishing.
