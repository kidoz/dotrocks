using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

/// <summary>
/// Groups every StarRocks-backed test class into one collection so they run serially against the
/// shared server: the tests mutate process-wide connection pools (ClearAllPools) and shared
/// databases, so parallel execution would interfere.
/// </summary>
[CollectionDefinition("StarRocks integration")]
[SuppressMessage(
    "Maintainability",
    "CA1515:Consider making public types internal",
    Justification = "xUnit collection definitions must be public to be discovered."
)]
public sealed class StarRocksIntegrationCollectionDefinition
    : ICollectionFixture<StarRocksIntegrationDatabaseFixture>;

/// <summary>
/// Owns the per-run transaction test database: the Guid-suffixed name prevents collisions between
/// concurrent runs against a shared server, and disposal drops the database so no residue
/// accumulates.
/// </summary>
[SuppressMessage(
    "Maintainability",
    "CA1515:Consider making public types internal",
    Justification = "xUnit collection fixtures must be public to be constructed by the framework."
)]
public sealed class StarRocksIntegrationDatabaseFixture : IAsyncLifetime
{
    /// <summary>The per-run database used by transaction and table-backed tests.</summary>
    public static string TransactionDatabaseName { get; } =
        "dotrocks_tx_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The database name is generated internally and never uses user input."
    )]
    public async ValueTask InitializeAsync()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        // Some tests put this database in their connection string without creating it (for
        // example a plain open/close), so it must exist before the first test in the collection
        // runs — test execution order is not guaranteed. The per-test
        // CREATE DATABASE IF NOT EXISTS calls stay as harmless no-ops.
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE IF NOT EXISTS {TransactionDatabaseName}";
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The database name is generated internally and never uses user input."
    )]
    public async ValueTask DisposeAsync()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        // Best-effort cleanup: a teardown failure must not mask test results.
        try
        {
            using var connection = new DotRocksConnection(
                IntegrationTestEnvironment.ConnectionString
            );
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {TransactionDatabaseName}";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DotRocksException)
        {
            // The server disappeared after the tests ran; nothing left to clean up.
        }
    }
}
