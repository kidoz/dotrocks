using System.Collections.ObjectModel;
using System.Data.Common;
using DotRocks.Data;
using DotRocks.Data.Protocol.Framing;
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
    public void GetColumnSchema_ExposesColumnMetadata()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [
                    Column("id", (byte)ColumnType.Long, flags: 1, columnLength: 11),
                    Column("amount", (byte)ColumnType.NewDecimal, columnLength: 18),
                    Column("name"),
                ],
                [
                    [7, 12.34m, "seven"],
                ]
            )
        );

        ReadOnlyCollection<DbColumn> schema = reader.GetColumnSchema();

        Assert.Equal(3, schema.Count);
        Assert.Equal("id", schema[0].ColumnName);
        Assert.Equal(0, schema[0].ColumnOrdinal);
        Assert.Equal(typeof(int), schema[0].DataType);
        Assert.Equal("LONG", schema[0].DataTypeName);
        Assert.False(schema[0].AllowDBNull);
        Assert.Equal(11, schema[0].ColumnSize);
        Assert.Equal("amount", schema[1].ColumnName);
        Assert.Equal(1, schema[1].ColumnOrdinal);
        Assert.Equal(typeof(decimal), schema[1].DataType);
        Assert.Equal("NEWDECIMAL", schema[1].DataTypeName);
        Assert.True(schema[1].AllowDBNull);
        Assert.Equal("name", schema[2].ColumnName);
        Assert.Equal(2, schema[2].ColumnOrdinal);
        Assert.Equal(typeof(string), schema[2].DataType);
        Assert.Equal("VAR_STRING", schema[2].DataTypeName);
    }

    [Fact]
    public async Task GetColumnSchemaAsync_ExposesColumnMetadata()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("id", (byte)ColumnType.Long)],
                [
                    [7],
                ]
            )
        );

        ReadOnlyCollection<DbColumn> schema = await reader
            .GetColumnSchemaAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Single(schema);
        Assert.Equal("id", schema[0].ColumnName);
    }

    [Fact]
    public void GetFieldValue_ReturnsTypedValuesMatchingTypedGetters()
    {
        var bytes = new byte[] { 0x00, 0xFF };
        var createdAt = new DateTime(2026, 6, 19, 13, 14, 15);
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [
                    Column("i32", (byte)ColumnType.Long),
                    Column("i64", (byte)ColumnType.LongLong),
                    Column("amount", (byte)ColumnType.NewDecimal),
                    Column("ratio", (byte)ColumnType.Double),
                    Column("single_value", (byte)ColumnType.Float),
                    Column("name"),
                    Column("created_at", (byte)ColumnType.DateTime),
                    Column("flag", (byte)ColumnType.Tiny),
                    Column("bytes", (byte)ColumnType.Blob),
                ],
                [
                    [7, 8L, 12.34m, 1.5d, 1.25f, "seven", createdAt, true, bytes],
                ]
            )
        );

        Assert.True(reader.Read());
        Assert.Equal(reader.GetInt32(0), reader.GetFieldValue<int>(0));
        Assert.Equal(reader.GetInt64(1), reader.GetFieldValue<long>(1));
        Assert.Equal(reader.GetDecimal(2), reader.GetFieldValue<decimal>(2));
        Assert.Equal(reader.GetDouble(3), reader.GetFieldValue<double>(3));
        Assert.Equal(reader.GetFloat(4), reader.GetFieldValue<float>(4));
        Assert.Equal(reader.GetString(5), reader.GetFieldValue<string>(5));
        Assert.Equal(reader.GetDateTime(6), reader.GetFieldValue<DateTime>(6));
        Assert.Equal(reader.GetBoolean(7), reader.GetFieldValue<bool>(7));
        Assert.Same(bytes, reader.GetFieldValue<byte[]>(8));
    }

    [Fact]
    public async Task GetFieldValueAsync_ReturnsTypedValue()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("i32", (byte)ColumnType.Long)],
                [
                    [7],
                ]
            )
        );

        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal(
            7,
            await reader
                .GetFieldValueAsync<int>(0, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact]
    public void GetFieldValue_HandlesDbNulls()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("value", (byte)ColumnType.Long)],
                [
                    [null],
                ]
            )
        );

        Assert.True(reader.Read());
        Assert.Same(DBNull.Value, reader.GetFieldValue<DBNull>(0));
        Assert.Same(DBNull.Value, reader.GetFieldValue<object>(0));
        Assert.Null(reader.GetFieldValue<int?>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<int>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<string>(0));
    }

    [Fact]
    public void GetFieldValue_InvalidCast_ThrowsInvalidCastException()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("value")],
                [
                    ["not-an-int"],
                ]
            )
        );

        Assert.True(reader.Read());
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<int>(0));
    }

    [Fact]
    public void ActiveReader_BlocksConnectionUntilCompleted()
    {
        using var connection = new DotRocksConnection();
        using var stream = new MemoryStream();
        var rowReader = new TextResultRowReader(
            new PacketReader(stream),
            [Column("value", (byte)ColumnType.Long)],
            connectionId: null
        );
        using var reader = new DotRocksDataReader(
            StreamingQueryResult.FromRows(rowReader.Columns, rowReader),
            connection
        );

        connection.SetActiveReader(reader);

        Assert.Throws<InvalidOperationException>(connection.ValidateNoActiveReader);

        connection.CompleteActiveReader(reader, reusable: true);
        connection.ValidateNoActiveReader();
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
        byte columnType = (byte)ColumnType.VarString,
        ushort flags = 0,
        uint columnLength = 1024
    ) =>
        new(
            "def",
            string.Empty,
            string.Empty,
            string.Empty,
            name,
            name,
            0x21,
            columnLength,
            columnType,
            flags,
            0
        );
}
