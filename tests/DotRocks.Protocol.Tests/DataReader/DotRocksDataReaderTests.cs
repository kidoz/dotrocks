using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using DotRocks.Data;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Results;
using DotRocks.Protocol.Tests.TestInfrastructure;
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
                    Column("bytes", (byte)ColumnType.Blob),
                    Column("name"),
                ],
                [
                    [7, DotRocksDecimal.Parse("12.34"), new byte[] { 0x00, 0xFF }, "seven"],
                ]
            )
        );

        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Equal("LONG", reader.GetDataTypeName(0));
        Assert.Equal(typeof(DotRocksDecimal), reader.GetFieldType(1));
        Assert.Equal("NEWDECIMAL", reader.GetDataTypeName(1));
        Assert.Equal(typeof(byte[]), reader.GetFieldType(2));
        Assert.Equal("BLOB", reader.GetDataTypeName(2));
        Assert.Equal(typeof(string), reader.GetFieldType(3));
        Assert.Equal("VAR_STRING", reader.GetDataTypeName(3));
    }

    [Fact]
    public void GetColumnSchema_ExposesColumnMetadata()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [
                    Column("id", (byte)ColumnType.Long, flags: 1, columnLength: 11),
                    Column("amount", (byte)ColumnType.NewDecimal, columnLength: 18),
                    Column("bytes", (byte)ColumnType.Blob, columnLength: 16),
                    Column("name"),
                ],
                [
                    [7, DotRocksDecimal.Parse("12.34"), new byte[] { 0x00, 0xFF }, "seven"],
                ]
            )
        );

        ReadOnlyCollection<DbColumn> schema = reader.GetColumnSchema();

        Assert.Equal(4, schema.Count);
        Assert.Equal("id", schema[0].ColumnName);
        Assert.Equal(0, schema[0].ColumnOrdinal);
        Assert.Equal(typeof(int), schema[0].DataType);
        Assert.Equal("LONG", schema[0].DataTypeName);
        Assert.False(schema[0].AllowDBNull);
        Assert.Equal(11, schema[0].ColumnSize);
        Assert.Equal("amount", schema[1].ColumnName);
        Assert.Equal(1, schema[1].ColumnOrdinal);
        Assert.Equal(typeof(DotRocksDecimal), schema[1].DataType);
        Assert.Equal("NEWDECIMAL", schema[1].DataTypeName);
        Assert.True(schema[1].AllowDBNull);
        Assert.Equal("bytes", schema[2].ColumnName);
        Assert.Equal(2, schema[2].ColumnOrdinal);
        Assert.Equal(typeof(byte[]), schema[2].DataType);
        Assert.Equal("BLOB", schema[2].DataTypeName);
        Assert.Equal(16, schema[2].ColumnSize);
        Assert.Equal("name", schema[3].ColumnName);
        Assert.Equal(3, schema[3].ColumnOrdinal);
        Assert.Equal(typeof(string), schema[3].DataType);
        Assert.Equal("VAR_STRING", schema[3].DataTypeName);
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
                    [
                        7,
                        8L,
                        DotRocksDecimal.Parse("12.34"),
                        1.5d,
                        1.25f,
                        "seven",
                        createdAt,
                        true,
                        bytes,
                    ],
                ]
            )
        );

        Assert.True(reader.Read());
        Assert.Equal(reader.GetInt32(0), reader.GetFieldValue<int>(0));
        Assert.Equal(reader.GetInt64(1), reader.GetFieldValue<long>(1));
        Assert.Equal(reader.GetDecimal(2), reader.GetFieldValue<decimal>(2));
        Assert.Equal(DotRocksDecimal.Parse("12.34"), reader.GetFieldValue<DotRocksDecimal>(2));
        Assert.Equal(reader.GetDouble(3), reader.GetFieldValue<double>(3));
        Assert.Equal(reader.GetFloat(4), reader.GetFieldValue<float>(4));
        Assert.Equal(reader.GetString(5), reader.GetFieldValue<string>(5));
        Assert.Equal(reader.GetDateTime(6), reader.GetFieldValue<DateTime>(6));
        Assert.Equal(reader.GetBoolean(7), reader.GetFieldValue<bool>(7));
        Assert.Same(bytes, reader.GetFieldValue<byte[]>(8));
    }

    [Fact]
    public void GetBytes_ReadsBinaryValues()
    {
        byte[] bytes = [0x00, 0xFF, 0x10, 0x20];
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("bytes", (byte)ColumnType.Blob)],
                [
                    [bytes],
                ]
            )
        );
        byte[] buffer = [0xAA, 0xAA, 0xAA, 0xAA, 0xAA];

        Assert.True(reader.Read());
        Assert.Equal(4, reader.GetBytes(0, 0, null, 0, 0));
        Assert.Equal(2, reader.GetBytes(0, 1, buffer, 2, 2));
        Assert.Equal([0xAA, 0xAA, 0xFF, 0x10, 0xAA], buffer);
        Assert.Equal(0, reader.GetBytes(0, 4, buffer, 0, 2));
    }

    [Fact]
    public void GetFieldValue_ParsesInt128FromLargeIntText()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("large_value", (byte)ColumnType.String, columnLength: 40)],
                [
                    ["170141183460469231731687303715884105727"],
                ]
            )
        );

        Assert.True(reader.Read());
        Assert.Equal(
            Int128.Parse("170141183460469231731687303715884105727", CultureInfo.InvariantCulture),
            reader.GetFieldValue<Int128>(0)
        );
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
    public void GetFieldValue_DecimalPrecisionLoss_ThrowsDotRocksPrecisionLossException()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("amount", (byte)ColumnType.NewDecimal)],
                [
                    [DotRocksDecimal.Parse("12345678901234567890123456789012345678.9000")],
                ]
            )
        );

        Assert.True(reader.Read());
        Assert.Throws<DotRocksPrecisionLossException>(() => reader.GetDecimal(0));
        Assert.Throws<DotRocksPrecisionLossException>(() => reader.GetFieldValue<decimal>(0));
        Assert.Equal(
            DotRocksDecimal.Parse("12345678901234567890123456789012345678.9000"),
            reader.GetFieldValue<DotRocksDecimal>(0)
        );
    }

    [Fact]
    public void GetFieldValue_ConvertsDateTimeStringAndGuidValues()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [
                    Column("date_value", (byte)ColumnType.Date),
                    Column("time_value", (byte)ColumnType.VarChar),
                    Column("guid_value", (byte)ColumnType.VarChar),
                ],
                [
                    [new DateTime(2026, 6, 19), "13:14:15", "9f4f591e-3db2-4879-856c-1c54b4241b76"],
                ]
            )
        );

        Assert.True(reader.Read());
        Assert.Equal(new DateOnly(2026, 6, 19), reader.GetFieldValue<DateOnly>(0));
        Assert.Equal(new TimeOnly(13, 14, 15), reader.GetFieldValue<TimeOnly>(1));
        Assert.Equal(
            Guid.Parse("9f4f591e-3db2-4879-856c-1c54b4241b76"),
            reader.GetFieldValue<Guid>(2)
        );
    }

    [Fact]
    public void ActiveReader_BlocksConnectionUntilCompleted()
    {
        using var connection = new DotRocksConnection();
        using var stream = new MemoryStream();
        var rowReader = ResultRowReader.ForText(
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
    public void HasRows_StreamingEmptyResult_ReturnsFalse()
    {
        using var stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 0,
            StarRocksPacketFactory.Eof()
        );
        var rowReader = ResultRowReader.ForText(
            new PacketReader(stream),
            [Column("value", (byte)ColumnType.Long)],
            connectionId: null
        );
        using var reader = new DotRocksDataReader(
            StreamingQueryResult.FromRows(rowReader.Columns, rowReader)
        );

        Assert.False(reader.HasRows);
        Assert.False(reader.Read());
    }

    [Fact]
    public void HasRows_StreamingResultWithRow_PreservesPrefetchedRowForRead()
    {
        using var stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 0,
            StarRocksPacketFactory.TextRow("42"),
            StarRocksPacketFactory.Eof()
        );
        var rowReader = ResultRowReader.ForText(
            new PacketReader(stream),
            [Column("value", (byte)ColumnType.Long)],
            connectionId: null
        );
        using var reader = new DotRocksDataReader(
            StreamingQueryResult.FromRows(rowReader.Columns, rowReader)
        );

        Assert.True(reader.HasRows);
        Assert.True(reader.Read());
        Assert.Equal(42, reader.GetInt32(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void GetString_BinaryColumn_ThrowsInvalidCast()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("bytes", (byte)ColumnType.Blob)],
                [
                    [new byte[] { 0x00, 0xFF }],
                ]
            )
        );
        Assert.True(reader.Read());

        // Convert.ToString(byte[]) would silently return "System.Byte[]"; binary values must
        // fail explicitly instead.
        Assert.Throws<InvalidCastException>(() => reader.GetString(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<string>(0));
    }

    [Fact]
    public void Read_SingleRowBehavior_SurfacesAtMostOneRow()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("value")],
                [
                    ["1"],
                    ["2"],
                ]
            ),
            connection: null,
            CommandBehavior.SingleRow
        );

        Assert.True(reader.Read());
        Assert.Equal("1", reader.GetValue(0));
        Assert.False(reader.Read());
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
    }

    [Fact]
    public void Read_SchemaOnlyBehavior_ExposesMetadataWithoutRows()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("id", (byte)ColumnType.Long)],
                [
                    [7],
                ]
            ),
            connection: null,
            CommandBehavior.SchemaOnly
        );

        Assert.Equal(1, reader.FieldCount);
        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Single(reader.GetColumnSchema());
        Assert.False(reader.HasRows);
        Assert.False(reader.Read());
    }

    [Fact]
    public void Close_PartiallyReadStreamingResult_DrainsRemainingRows()
    {
        using var stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 0,
            StarRocksPacketFactory.TextRow("1"),
            StarRocksPacketFactory.TextRow("2"),
            StarRocksPacketFactory.TextRow("3"),
            StarRocksPacketFactory.Eof()
        );
        var rowReader = ResultRowReader.ForText(
            new PacketReader(stream),
            [Column("value", (byte)ColumnType.Long)],
            connectionId: null
        );
        var reader = new DotRocksDataReader(
            StreamingQueryResult.FromRows(rowReader.Columns, rowReader)
        );

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        reader.Close();

        // Closing after a partial read must consume the remaining rows and the EOF terminator so
        // the connection is left at a clean packet boundary.
        Assert.True(rowReader.IsConsumed);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void Read_StreamingSingleRowBehavior_LeavesRemainingRowsForCloseToDrain()
    {
        using var stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 0,
            StarRocksPacketFactory.TextRow("1"),
            StarRocksPacketFactory.TextRow("2"),
            StarRocksPacketFactory.Eof()
        );
        var rowReader = ResultRowReader.ForText(
            new PacketReader(stream),
            [Column("value", (byte)ColumnType.Long)],
            connectionId: null
        );
        var reader = new DotRocksDataReader(
            StreamingQueryResult.FromRows(rowReader.Columns, rowReader),
            connection: null,
            CommandBehavior.SingleRow
        );

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.False(reader.Read());

        reader.Close();
        Assert.True(rowReader.IsConsumed);
        Assert.Equal(stream.Length, stream.Position);
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

    [Fact]
    public void GetSchemaTable_ExposesStandardSchemaColumns()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("id", (byte)ColumnType.Long, flags: 1, columnLength: 11), Column("name")],
                [
                    [7, "seven"],
                ]
            )
        );

        DataTable schema = reader.GetSchemaTable();

        Assert.Equal(2, schema.Rows.Count);
        Assert.Equal("id", schema.Rows[0]["ColumnName"]);
        Assert.Equal(0, schema.Rows[0]["ColumnOrdinal"]);
        Assert.Equal(typeof(int), schema.Rows[0]["DataType"]);
        Assert.False((bool)schema.Rows[0]["AllowDBNull"]);
        Assert.Equal("name", schema.Rows[1]["ColumnName"]);
        Assert.True((bool)schema.Rows[1]["AllowDBNull"]);
    }

    [Fact]
    public void GetChars_CopiesCharacterWindow()
    {
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("name")],
                [
                    ["seven"],
                ]
            )
        );
        Assert.True(reader.Read());

        Assert.Equal(5, reader.GetChars(0, 0, buffer: null, bufferOffset: 0, length: 0));
        char[] buffer = new char[3];
        long copied = reader.GetChars(0, dataOffset: 1, buffer, bufferOffset: 0, length: 3);
        Assert.Equal(3, copied);
        Assert.Equal("eve", new string(buffer));
    }

    [Fact]
    public void GetGuid_ReadsGuidAndBinaryAndStringValues()
    {
        var guid = Guid.Parse("9f4f591e-3db2-4879-856c-1c54b4241b76");
        using var reader = new DotRocksDataReader(
            QueryResult.FromRows(
                [Column("g_native"), Column("g_bytes", (byte)ColumnType.Blob), Column("g_text")],
                [
                    [guid, guid.ToByteArray(), guid.ToString()],
                ]
            )
        );
        Assert.True(reader.Read());

        Assert.Equal(guid, reader.GetGuid(0));
        Assert.Equal(guid, reader.GetGuid(1));
        Assert.Equal(guid, reader.GetGuid(2));
    }

    [Fact]
    public void NextResult_WalksMultipleBufferedResults()
    {
        using var reader = new DotRocksDataReader(
            new[]
            {
                QueryResult.FromRows(
                    [Column("a")],
                    [
                        ["1"],
                    ]
                ),
                QueryResult.FromRows(
                    [Column("b"), Column("c")],
                    [
                        ["x", "y"],
                        ["p", "q"],
                    ]
                ),
            },
            connection: null,
            behavior: CommandBehavior.Default
        );

        Assert.Equal(1, reader.FieldCount);
        Assert.Equal("a", reader.GetName(0));
        Assert.True(reader.Read());
        Assert.Equal("1", reader.GetValue(0));
        Assert.False(reader.Read());

        Assert.True(reader.NextResult());
        Assert.Equal(2, reader.FieldCount);
        Assert.Equal("b", reader.GetName(0));
        Assert.Equal("c", reader.GetName(1));
        Assert.True(reader.Read());
        Assert.Equal("x", reader.GetValue(0));
        Assert.Equal("y", reader.GetValue(1));
        Assert.True(reader.Read());
        Assert.Equal("p", reader.GetValue(0));
        Assert.False(reader.Read());

        Assert.False(reader.NextResult());
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
