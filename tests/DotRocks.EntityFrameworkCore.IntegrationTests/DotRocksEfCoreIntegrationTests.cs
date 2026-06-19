using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotRocks.EntityFrameworkCore.IntegrationTests;

public sealed class DotRocksEfCoreIntegrationTests
{
    [Fact]
    public void UseStarRocks_ConfiguresProviderName()
    {
        var optionsBuilder = new DbContextOptionsBuilder<DotRocksTestContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");

        using var context = new DotRocksTestContext(optionsBuilder.Options);

        Assert.Equal("DotRocks.EntityFrameworkCore", context.Database.ProviderName);
    }

    [Fact]
    public async Task UseStarRocks_GetDbConnection_ExecutesSelectOne()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            return;
        }

        var optionsBuilder = new DbContextOptionsBuilder<DotRocksTestContext>();
        optionsBuilder.UseStarRocks(IntegrationTestEnvironment.ConnectionString);

        using var context = new DotRocksTestContext(optionsBuilder.Options);
        DbConnection connection = context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, value);
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class DotRocksTestContext(DbContextOptions<DotRocksTestContext> options)
        : DbContext(options);
}
