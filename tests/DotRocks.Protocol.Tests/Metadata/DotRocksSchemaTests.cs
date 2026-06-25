using System.Data;
using DotRocks.Data;
using Xunit;

namespace DotRocks.Protocol.Tests.Metadata;

public sealed class DotRocksSchemaTests
{
    [Fact]
    public void GetSchema_MetaDataCollections_ListsSupportedCollections()
    {
        using var connection = new DotRocksConnection();

        // MetaDataCollections is static and needs no open connection.
        DataTable collections = connection.GetSchema();

        Assert.Equal("MetaDataCollections", collections.TableName, StringComparer.Ordinal);
        string[] names = collections
            .Rows.Cast<DataRow>()
            .Select(row => (string)row["CollectionName"])
            .ToArray();
        Assert.Contains("Tables", names);
        Assert.Contains("Columns", names);
        Assert.Contains("Databases", names);
        Assert.Contains("Views", names);
    }

    [Fact]
    public void GetSchema_UnknownCollection_Throws()
    {
        using var connection = new DotRocksConnection();

        Assert.Throws<ArgumentException>(() => connection.GetSchema("NotACollection"));
    }
}
