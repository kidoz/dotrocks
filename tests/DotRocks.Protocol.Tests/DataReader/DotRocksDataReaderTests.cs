using DotRocks.Data;
using DotRocks.Data.Protocol.Results;
using Xunit;

namespace DotRocks.Protocol.Tests.DataReader;

public sealed class DotRocksDataReaderTests
{
    [Fact]
    public void Read_ExposesTextValuesAndNulls()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("id"), Column("name"), Column("missing")],
                [
                    ["7", "seven", null],
                ]
            )
        );

        Assert.Equal(3, reader.FieldCount);
        Assert.True(reader.HasRows);
        Assert.True(reader.Read());
        Assert.Equal("id", reader.GetName(0));
        Assert.Equal(7, reader.GetInt32(0));
        Assert.Equal("seven", reader["name"]);
        Assert.True(reader.IsDBNull(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Metadata_ExposesMappedColumnTypes()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [
                    Column("id", (byte)ColumnType.Long),
                    Column("amount", (byte)ColumnType.NewDecimal),
                    Column("name"),
                ],
                [
                    [7, 12.34m, "seven"],
                ]
            )
        );

        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Equal("LONG", reader.GetDataTypeName(0));
        Assert.Equal(typeof(decimal), reader.GetFieldType(1));
        Assert.Equal("NEWDECIMAL", reader.GetDataTypeName(1));
        Assert.Equal(typeof(string), reader.GetFieldType(2));
        Assert.Equal("VAR_STRING", reader.GetDataTypeName(2));
    }

    [Fact]
    public void Read_BeforeRow_Throws()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("value")],
                [
                    ["x"],
                ]
            )
        );

        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
    }

    private static ColumnDefinition Column(
        string name,
        byte columnType = (byte)ColumnType.VarString
    ) =>
        new(
            "def",
            string.Empty,
            string.Empty,
            string.Empty,
            name,
            name,
            0x21,
            1024,
            columnType,
            0,
            0
        );
}
