# DotRocks task shortcuts. Run `just` or `just --list` to see recipes.
# Requires the .NET 10 SDK; Docker is required only for the integration/harness recipes.

set shell := ["bash", "-cu"]

# StarRocks connection defaults (override on the command line, e.g. `just harness 10.0.0.5 9030`).
fe_host := env_var_or_default("DOTROCKS_HOST", "127.0.0.1")
fe_port := env_var_or_default("DOTROCKS_PORT", "9030")
config := env_var_or_default("CONFIG", "Release")

# StarRocks test-server Docker Compose file.
compose_file := "dev/docker-compose.yml"

# Show available recipes.
default:
    @just --list

# Restore local tools (CSharpier) and NuGet packages with the committed lock state.
restore:
    dotnet tool restore
    dotnet restore --locked-mode

# Apply CSharpier formatting to the whole repository.
format:
    dotnet csharpier format .

# Verify formatting without modifying files (CI gate).
format-check:
    dotnet csharpier check .

# Build the solution (warnings are errors).
build:
    dotnet build DotRocks.slnx --configuration {{config}} --no-restore

# Run the server-free unit and protocol tests.
test:
    dotnet test DotRocks.slnx --configuration {{config}} --no-build

# Run integration tests against an already-running StarRocks FE.
integration-test:
    DOTROCKS_RUN_INTEGRATION=1 dotnet test tests/DotRocks.Data.IntegrationTests --configuration {{config}} --no-build
    DOTROCKS_RUN_INTEGRATION=1 dotnet test tests/DotRocks.Data.DapperTests --configuration {{config}} --no-build
    DOTROCKS_RUN_INTEGRATION=1 dotnet test tests/DotRocks.EntityFrameworkCore.IntegrationTests --configuration {{config}} --no-build

# Produce NuGet packages.
pack:
    dotnet pack DotRocks.slnx --configuration {{config}} --no-build

# Full local gate: format check, build, and test.
ci: restore format-check build test

# Remove build output.
clean:
    dotnet clean DotRocks.slnx --configuration {{config}} || true
    rm -rf artifacts

# --- Integration / Phase-Zero (Docker required) ---

# Start the pinned StarRocks container and wait until it is genuinely query-ready.
starrocks-up:
    #!/usr/bin/env bash
    set -euo pipefail
    compose_file="{{compose_file}}"
    fe_query_port="${DOTROCKS_FE_QUERY_PORT:-9030}"
    fe_http_port="${DOTROCKS_FE_HTTP_PORT:-8030}"
    max_attempts="${DOTROCKS_READY_ATTEMPTS:-120}"
    poll_seconds="${DOTROCKS_READY_POLL_SECONDS:-5}"

    echo "Starting StarRocks (compose: ${compose_file})..."
    docker compose -f "${compose_file}" up -d

    echo "Waiting for the FE query port ${fe_query_port} to answer SELECT 1..."
    attempt=0
    until docker compose -f "${compose_file}" exec -T starrocks \
        mysql -h127.0.0.1 -P9030 -uroot -e 'SELECT 1' >/dev/null 2>&1; do
        attempt=$((attempt + 1))
        if [ "${attempt}" -ge "${max_attempts}" ]; then
            echo "StarRocks FE did not become query-ready within $((max_attempts * poll_seconds))s." >&2
            docker compose -f "${compose_file}" logs --tail=100 starrocks >&2 || true
            exit 1
        fi
        sleep "${poll_seconds}"
    done
    echo "FE query endpoint is ready."

    echo "Waiting for the FE HTTP / Stream Load endpoint on ${fe_http_port}..."
    attempt=0
    until curl -fsS -o /dev/null "http://127.0.0.1:${fe_http_port}/api/health" 2>/dev/null \
        || curl -fsS -o /dev/null "http://127.0.0.1:${fe_http_port}/" 2>/dev/null; do
        attempt=$((attempt + 1))
        if [ "${attempt}" -ge "${max_attempts}" ]; then
            echo "StarRocks FE HTTP endpoint did not respond in time." >&2
            exit 1
        fi
        sleep "${poll_seconds}"
    done
    echo "FE HTTP / Stream Load endpoint is ready."

    echo "StarRocks is ready: query=127.0.0.1:${fe_query_port}, http=127.0.0.1:${fe_http_port}"

# Stop the StarRocks container and remove its volumes.
starrocks-down:
    #!/usr/bin/env bash
    set -euo pipefail
    compose_file="{{compose_file}}"
    echo "Stopping StarRocks (compose: ${compose_file})..."
    docker compose -f "${compose_file}" down -v
    echo "StarRocks stopped and volumes removed."

# Probe the StarRocks handshake with the compatibility harness.
harness host=fe_host port=fe_port:
    dotnet run --project tests/DotRocks.CompatibilityHarness --configuration {{config}} -- {{host}} {{port}}

# Run the budgeted BenchmarkDotNet suite (optionally filtered, e.g. `just bench '*Parse*'`).
bench filter='*':
    dotnet run --project benchmarks/DotRocks.Benchmarks --configuration Release -- --filter '{{filter}}'

# Build the DocFX documentation site into artifacts/docfx/_site.
docs:
    dotnet tool restore
    dotnet docfx docs/docfx.json

# Build and serve the DocFX documentation site locally.
docs-serve:
    dotnet tool restore
    dotnet docfx docs/docfx.json --serve
