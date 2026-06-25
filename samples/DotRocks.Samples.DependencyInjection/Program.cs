using System.Data.Common;
using DotRocks.Data;
using Microsoft.Extensions.DependencyInjection;

// DotRocks.Data is intentionally dependency-free, so it ships no DI extension method.
// DotRocksDataSource is a DbDataSource, which the standard container wires up in a few lines:
// register the data source as a singleton and resolve scoped connections from it.
string connectionString =
    Environment.GetEnvironmentVariable("DOTROCKS_CONNECTION_STRING")
    ?? "Server=127.0.0.1;Port=9030;User ID=root;Database=dotrocks_sample";

var services = new ServiceCollection();
services.AddSingleton<DbDataSource>(_ => new DotRocksDataSource(connectionString));
services.AddScoped<DbConnection>(provider =>
    provider.GetRequiredService<DbDataSource>().CreateConnection()
);

await using ServiceProvider provider = services.BuildServiceProvider();

await using (AsyncServiceScope scope = provider.CreateAsyncScope())
{
    var connection = scope.ServiceProvider.GetRequiredService<DbConnection>();
    await connection.OpenAsync();

    await using DbCommand command = connection.CreateCommand();
    command.CommandText = "SELECT 1";
    object? result = await command.ExecuteScalarAsync();
    Console.WriteLine($"Connected via DI, server returned: {result}");
}
