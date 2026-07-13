using DotRocks.Data;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.Results;

public sealed class TextResultParserTests
{
    [Fact]
    public async Task ReadAsync_ParsesSingleColumnTextResult()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 1,
            StarRocksPacketFactory.ColumnDefinition("answer"),
            StarRocksPacketFactory.Eof(),
            StarRocksPacketFactory.TextRow("42"),
            StarRocksPacketFactory.Eof()
        );
        var packetReader = new PacketReader(stream);
        packetReader.ResetSequence(1);

        QueryResult result = await TextResultParser.ReadAsync([0x01], packetReader, null, ct);

        Assert.True(result.HasResultSet);
        Assert.Equal(-1, result.RecordsAffected);
        Assert.Single(result.Columns);
        Assert.Equal("answer", result.Columns[0].Name);
        Assert.Single(result.Rows);
        Assert.Equal("42", result.Rows[0][0]);
    }

    [Fact]
    public async Task ReadAsync_ParsesNullTextValue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 1,
            StarRocksPacketFactory.ColumnDefinition("value"),
            StarRocksPacketFactory.Eof(),
            [ProtocolConstants.NullValueMarker],
            StarRocksPacketFactory.Eof()
        );
        var packetReader = new PacketReader(stream);
        packetReader.ResetSequence(1);

        QueryResult result = await TextResultParser.ReadAsync([0x01], packetReader, null, ct);

        Assert.Null(result.Rows[0][0]);
    }

    [Fact]
    public async Task ReadAsync_EmptyLeadingColumnValue_DoesNotTruncateResult()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 1,
            StarRocksPacketFactory.ColumnDefinition("value"),
            StarRocksPacketFactory.Eof(),
            StarRocksPacketFactory.TextRow(""),
            StarRocksPacketFactory.TextRow("second"),
            StarRocksPacketFactory.Eof()
        );
        var packetReader = new PacketReader(stream);
        packetReader.ResetSequence(1);

        QueryResult result = await TextResultParser.ReadAsync([0x01], packetReader, null, ct);

        // A row whose first column is the empty string (0x00) must not be mistaken for an
        // OK terminator and silently drop this and every following row.
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("", result.Rows[0][0]);
        Assert.Equal("second", result.Rows[1][0]);
    }

    [Fact]
    public async Task ResultRowReader_EmptyLeadingColumnValue_DoesNotTerminateEarly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 1,
            StarRocksPacketFactory.TextRow(""),
            StarRocksPacketFactory.TextRow("second"),
            StarRocksPacketFactory.Eof()
        );
        var packetReader = new PacketReader(stream);
        packetReader.ResetSequence(1);
        ColumnDefinition[] columns = [Column("value")];
        var rowReader = ResultRowReader.ForText(packetReader, columns, connectionId: null);

        var rows = new List<object?[]>();
        object?[]? row;
        while ((row = await rowReader.ReadRowAsync(ct)) is not null)
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal("", rows[0][0]);
        Assert.Equal("second", rows[1][0]);
    }

    [Fact]
    public async Task ReadAsync_ParsesTypedTextValues()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 1,
            StarRocksPacketFactory.ColumnDefinition("i32", (byte)ColumnType.Long),
            StarRocksPacketFactory.ColumnDefinition("i64", (byte)ColumnType.LongLong),
            StarRocksPacketFactory.ColumnDefinition("amount", (byte)ColumnType.NewDecimal),
            StarRocksPacketFactory.ColumnDefinition("ratio", (byte)ColumnType.Double),
            StarRocksPacketFactory.ColumnDefinition("created_on", (byte)ColumnType.Date),
            StarRocksPacketFactory.ColumnDefinition("created_at", (byte)ColumnType.DateTime),
            StarRocksPacketFactory.Eof(),
            StarRocksPacketFactory.TextRow(
                "42",
                "9007199254740991",
                "12.34",
                "1.5",
                "2026-06-19",
                "2026-06-19 13:14:15"
            ),
            StarRocksPacketFactory.Eof()
        );
        var packetReader = new PacketReader(stream);
        packetReader.ResetSequence(1);

        QueryResult result = await TextResultParser.ReadAsync([0x06], packetReader, null, ct);

        object?[] row = result.Rows[0];
        Assert.Equal(42, row[0]);
        Assert.Equal(9007199254740991L, row[1]);
        Assert.Equal(DotRocksDecimal.Parse("12.34"), row[2]);
        Assert.Equal(1.5d, row[3]);
        Assert.Equal(new DateTime(2026, 6, 19), row[4]);
        Assert.Equal(new DateTime(2026, 6, 19, 13, 14, 15), row[5]);
    }

    [Fact]
    public async Task ReadAsync_PreservesBlobValuesAsBytes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 1,
            StarRocksPacketFactory.ColumnDefinition("bytes", (byte)ColumnType.Blob),
            StarRocksPacketFactory.Eof(),
            StarRocksPacketFactory.BinaryRow([0x00, 0xFF, 0x10]),
            StarRocksPacketFactory.Eof()
        );
        var packetReader = new PacketReader(stream);
        packetReader.ResetSequence(1);

        QueryResult result = await TextResultParser.ReadAsync([0x01], packetReader, null, ct);

        byte[] bytes = Assert.IsType<byte[]>(result.Rows[0][0]);
        Assert.Equal([0x00, 0xFF, 0x10], bytes);
    }

    [Fact]
    public async Task ReadAsync_ParsesOkResult()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        var packetReader = new PacketReader(stream);

        QueryResult result = await TextResultParser.ReadAsync(
            StarRocksPacketFactory.Ok(3),
            packetReader,
            null,
            ct
        );

        Assert.False(result.HasResultSet);
        Assert.Equal(3, result.RecordsAffected);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task ReadBinaryStreamingAsync_ReturnsMetadataBeforeReadingRows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] binaryRow = [0x00, 0x00, 0x02, (byte)'h', (byte)'i'];
        using MemoryStream stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 1,
            StarRocksPacketFactory.ColumnDefinition("value"),
            StarRocksPacketFactory.Eof(),
            binaryRow,
            StarRocksPacketFactory.Eof()
        );
        var packetReader = new PacketReader(stream);
        packetReader.ResetSequence(1);

        StreamingQueryResult result = await TextResultParser.ReadBinaryStreamingAsync(
            [0x01],
            packetReader,
            null,
            ct
        );

        Assert.True(result.HasResultSet);
        Assert.NotNull(result.RowReader);
        Assert.True(stream.Position < stream.Length);

        object?[]? row = await result.RowReader.ReadRowAsync(ct);
        Assert.NotNull(row);
        Assert.Equal("hi", row[0]);
        Assert.Null(await result.RowReader.ReadRowAsync(ct));
    }

    [Fact]
    public async Task ReadAsync_ErrorPacket_ThrowsDotRocksException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        var packetReader = new PacketReader(stream);

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await TextResultParser
                    .ReadAsync(StarRocksPacketFactory.Error(), packetReader, 42, ct)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(1064, exception.ServerErrorCode);
        Assert.Equal("42000", exception.SqlState);
        Assert.Equal(42u, exception.ConnectionId);
        Assert.Contains("syntax error", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadColumnCount_WhenCountExceedsInt32_ThrowsMalformedPacketException()
    {
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedInteger((ulong)int.MaxValue + 1UL);

        MalformedPacketException exception = Assert.Throws<MalformedPacketException>(() =>
            TextResultParser.ReadColumnCount(writer.WrittenSpan)
        );

        Assert.Contains("column count", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadColumnCount_WhenCountExceedsSupportedMaximum_ThrowsMalformedPacketException()
    {
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedInteger(65_536);

        MalformedPacketException exception = Assert.Throws<MalformedPacketException>(() =>
            TextResultParser.ReadColumnCount(writer.WrittenSpan)
        );

        // A hostile column count must be rejected before the parser pre-allocates a per-column list.
        Assert.Contains("maximum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadError_StripsControlCharactersFromServerMessage()
    {
        DotRocksException exception = ResultPacket.ReadError(
            StarRocksPacketFactory.Error("line1\r\nFAKE LOG ENTRY\tcol"),
            connectionId: null
        );

        // Untrusted server text must not be able to forge log lines via embedded CR/LF.
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\n', exception.Message);
        Assert.DoesNotContain('\t', exception.Message);
        Assert.Contains("line1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FAKE LOG ENTRY", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadTextRow_WhenValueLengthClaimExceedsPayload_ThrowsMalformedPacketException()
    {
        ColumnDefinition[] columns = [Column("value")];
        byte[] payload = [0x05, 0x01, 0x02];

        MalformedPacketException exception = Assert.Throws<MalformedPacketException>(() =>
            TextResultParser.ReadTextRow(payload, columns)
        );

        Assert.Contains("claims", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadTextRow_WithTrailingBytes_ThrowsMalformedPacketException()
    {
        ColumnDefinition[] columns = [Column("value")];
        byte[] payload = StarRocksPacketFactory.TextRow("ok");
        byte[] withTrailing = [.. payload, 0xAA];

        MalformedPacketException exception = Assert.Throws<MalformedPacketException>(() =>
            TextResultParser.ReadTextRow(withTrailing, columns)
        );

        Assert.Contains("trailing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadOk_WithInvalidHeader_ThrowsMalformedPacketException()
    {
        MalformedPacketException exception = Assert.Throws<MalformedPacketException>(() =>
            ResultPacket.ReadOk([0x01])
        );

        Assert.Contains("OK packet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOk_WithTruncatedPayload_ThrowsMalformedPacketException()
    {
        MalformedPacketException exception = Assert.Throws<MalformedPacketException>(() =>
            ResultPacket.ReadOk([ResultPacket.OkHeader])
        );

        Assert.Contains("packet", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadOk_WhenAffectedRowsExceedsInt64_ThrowsMalformedPacketException()
    {
        using var writer = new ProtocolWriter();
        writer.WriteByte(ResultPacket.OkHeader);
        writer.WriteLengthEncodedInteger((ulong)long.MaxValue + 1UL);
        writer.WriteLengthEncodedInteger(0);

        MalformedPacketException exception = Assert.Throws<MalformedPacketException>(() =>
            ResultPacket.ReadOk(writer.WrittenSpan)
        );

        Assert.Contains("affected-row", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadError_WithInvalidHeader_ThrowsMalformedPacketException()
    {
        MalformedPacketException exception = Assert.Throws<MalformedPacketException>(() =>
            ResultPacket.ReadError([0x00], null)
        );

        Assert.Contains("ERR packet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadError_WithTruncatedPayload_ThrowsMalformedPacketException()
    {
        MalformedPacketException exception = Assert.Throws<MalformedPacketException>(() =>
            ResultPacket.ReadError([ProtocolConstants.ErrorPacketHeader, 0x01], null)
        );

        Assert.Contains("packet", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadError_WithTruncatedSqlStateMarker_ThrowsMalformedPacketException()
    {
        MalformedPacketException exception = Assert.Throws<MalformedPacketException>(() =>
            ResultPacket.ReadError(
                [ProtocolConstants.ErrorPacketHeader, 0x28, 0x04, (byte)'#', (byte)'4'],
                null
            )
        );

        Assert.Contains("SQLSTATE", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedColumnDefinitionPayloads))]
    public void ReadColumnDefinition_MalformedPayload_ThrowsMalformedPacketException(byte[] payload)
    {
        Assert.Throws<MalformedPacketException>(() =>
            TextResultParser.ReadColumnDefinition(payload)
        );
    }

    [Fact]
    public async Task ReadAsync_MalformedColumnDefinitionPacket_ThrowsMalformedPacketException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = StarRocksPacketFactory.PayloadStream(
            firstSequenceId: 1,
            [ProtocolConstants.NullValueMarker]
        );
        var packetReader = new PacketReader(stream);
        packetReader.ResetSequence(1);

        await Assert
            .ThrowsAsync<MalformedPacketException>(async () =>
                await TextResultParser
                    .ReadAsync([0x01], packetReader, null, ct)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
    }

    public static TheoryData<byte[]> MalformedColumnDefinitionPayloads() =>
        new()
        {
            { [] },
            { [ProtocolConstants.NullValueMarker] },
            { StarRocksPacketFactory.ColumnDefinition("bad_length", fixedFieldsLength: 0x0B) },
            { StarRocksPacketFactory.ColumnDefinition("truncated")[..^1] },
            { [.. StarRocksPacketFactory.ColumnDefinition("trailing"), 0xAA] },
        };

    private static ColumnDefinition Column(string name) =>
        TextResultParser.ReadColumnDefinition(StarRocksPacketFactory.ColumnDefinition(name));
}
