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

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

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
