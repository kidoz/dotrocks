# DotRocks task shortcuts. Run `just` or `just --list` to see recipes.
# Requires the .NET 10 SDK; Docker is required only for the integration/harness recipes.

set shell := ["bash", "-cu"]

# StarRocks connection defaults (override on the command line, e.g. `just harness 10.0.0.5 9030`).
fe_host := env_var_or_default("DOTROCKS_HOST", "127.0.0.1")
fe_port := env_var_or_default("DOTROCKS_PORT", "9030")
config := env_var_or_default("CONFIG", "Release")

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

# Start the pinned StarRocks container and wait until it is query-ready.
starrocks-up:
    ./eng/scripts/start-starrocks.sh

# Stop the StarRocks container and remove its volumes.
starrocks-down:
    ./eng/scripts/stop-starrocks.sh

# Probe the StarRocks handshake with the compatibility harness.
harness host=fe_host port=fe_port:
    dotnet run --project tests/DotRocks.CompatibilityHarness --configuration {{config}} -- {{host}} {{port}}
