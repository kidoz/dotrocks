using System.Data.Common;
using DotRocks.Data;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class DotRocksFactoryTests
{
    [Fact]
    public void Instance_CreatesProviderObjects()
    {
        DotRocksFactory factory = DotRocksFactory.Instance;

        using DbConnection connection = Assert.IsType<DotRocksConnection>(
            factory.CreateConnection()
        );
        using DbCommand command = Assert.IsType<DotRocksCommand>(factory.CreateCommand());
        DbParameter parameter = Assert.IsType<DotRocksParameter>(factory.CreateParameter());
        DbConnectionStringBuilder builder = Assert.IsType<DotRocksConnectionStringBuilder>(
            factory.CreateConnectionStringBuilder()
        );

        Assert.NotNull(parameter);
        Assert.NotNull(builder);
        Assert.Same(factory, DbProviderFactories.GetFactory(connection));
        Assert.Empty(command.CommandText);
    }

    [Fact]
    public void Connection_CreateCommand_ReturnsTypedCommand()
    {
        using var connection = new DotRocksConnection();

        using DotRocksCommand command = connection.CreateCommand();

        Assert.Same(connection, command.Connection);
    }

    [Fact]
    public void CreateDataSource_ReturnsDotRocksDataSource()
    {
        DbDataSource dataSource = DotRocksFactory.Instance.CreateDataSource(
            "Host=starrocks.local;User=alice;Pooling=true"
        );

        using (dataSource)
        {
            var dotRocksDataSource = Assert.IsType<DotRocksDataSource>(dataSource);

            Assert.Contains(
                "Server=starrocks.local",
                dotRocksDataSource.ConnectionString,
                StringComparison.Ordinal
            );
            Assert.Contains(
                "User ID=alice",
                dotRocksDataSource.ConnectionString,
                StringComparison.Ordinal
            );
            Assert.Contains(
                "Pooling=True",
                dotRocksDataSource.ConnectionString,
                StringComparison.Ordinal
            );
        }
    }
}
