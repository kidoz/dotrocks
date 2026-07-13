using System.Text;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Results;

/// <summary>
/// Parses result sets from the wire. The text and binary protocols share the same response shape
/// (first payload, column definitions, terminator, row packets) and differ only in how a row
/// payload is decoded, so the shared preamble and row loop live here and the row encoding is
/// selected via <see cref="ResultRowReader"/>.
/// </summary>
internal static class TextResultParser
{
    // Upper bound on the column count accepted from the server before any column packet is read.
    // The count is a length-encoded integer that drives a list pre-allocation, so an unbounded
    // value would let a hostile or corrupt server force a multi-gigabyte allocation (OOM) up front.
    // 65535 is far above any realistic result-set width while keeping the pre-allocation small.
    private const int MaxColumnCount = 65535;

    public static async ValueTask<QueryResult> ReadAsync(
        byte[] firstPayload,
        PacketReader reader,
        uint? connectionId,
        CancellationToken cancellationToken
    )
    {
        (OkResult? ok, List<ColumnDefinition>? columns) = await ReadResultSetHeaderAsync(
                firstPayload,
                reader,
                connectionId,
                okTerminatesColumns: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (ok is { } okResult)
        {
            return QueryResult.FromOk(okResult);
        }

        List<object?[]> rows = await ReadAllRowsAsync(
                ResultRowReader.ForText(reader, columns!, connectionId),
                cancellationToken
            )
            .ConfigureAwait(false);
        return QueryResult.FromRows(columns!, rows);
    }

    public static async ValueTask<StreamingQueryResult> ReadStreamingAsync(
        byte[] firstPayload,
        PacketReader reader,
        uint? connectionId,
        CancellationToken cancellationToken
    )
    {
        (OkResult? ok, List<ColumnDefinition>? columns) = await ReadResultSetHeaderAsync(
                firstPayload,
                reader,
                connectionId,
                okTerminatesColumns: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        return ok is { } okResult
            ? StreamingQueryResult.FromOk(okResult)
            : StreamingQueryResult.FromRows(
                columns!,
                ResultRowReader.ForText(reader, columns!, connectionId)
            );
    }

    public static StreamingQueryResult ReadStreaming(
        byte[] firstPayload,
        PacketReader reader,
        uint? connectionId
    )
    {
        (OkResult? ok, List<ColumnDefinition>? columns) = ReadResultSetHeader(
            firstPayload,
            reader,
            connectionId,
            okTerminatesColumns: true
        );
        return ok is { } okResult
            ? StreamingQueryResult.FromOk(okResult)
            : StreamingQueryResult.FromRows(
                columns!,
                ResultRowReader.ForText(reader, columns!, connectionId)
            );
    }

    public static async ValueTask<StreamingQueryResult> ReadBinaryStreamingAsync(
        byte[] firstPayload,
        PacketReader reader,
        uint? connectionId,
        CancellationToken cancellationToken
    )
    {
        (OkResult? ok, List<ColumnDefinition>? columns) = await ReadResultSetHeaderAsync(
                firstPayload,
                reader,
                connectionId,
                okTerminatesColumns: false,
                cancellationToken
            )
            .ConfigureAwait(false);
        return ok is { } okResult
            ? StreamingQueryResult.FromOk(okResult)
            : StreamingQueryResult.FromRows(
                columns!,
                ResultRowReader.ForBinary(reader, columns!, connectionId)
            );
    }

    public static StreamingQueryResult ReadBinaryStreaming(
        byte[] firstPayload,
        PacketReader reader,
        uint? connectionId
    )
    {
        (OkResult? ok, List<ColumnDefinition>? columns) = ReadResultSetHeader(
            firstPayload,
            reader,
            connectionId,
            okTerminatesColumns: false
        );
        return ok is { } okResult
            ? StreamingQueryResult.FromOk(okResult)
            : StreamingQueryResult.FromRows(
                columns!,
                ResultRowReader.ForBinary(reader, columns!, connectionId)
            );
    }

    public static QueryResult Read(byte[] firstPayload, PacketReader reader, uint? connectionId)
    {
        (OkResult? ok, List<ColumnDefinition>? columns) = ReadResultSetHeader(
            firstPayload,
            reader,
            connectionId,
            okTerminatesColumns: true
        );
        if (ok is { } okResult)
        {
            return QueryResult.FromOk(okResult);
        }

        var rowReader = ResultRowReader.ForText(reader, columns!, connectionId);
        var rows = new List<object?[]>();
        while (rowReader.ReadRow() is { } row)
        {
            rows.Add(row);
        }

        return QueryResult.FromRows(columns!, rows);
    }

    /// <summary>
    /// Reads the result-set preamble shared by the text and binary protocols: classifies the first
    /// response payload (ERR, OK, LOCAL INFILE, or a column count), then consumes the
    /// column-definition packets and their terminator. Returns the OK result when the response
    /// carries no result set; otherwise returns the column list (exactly one of the two is set).
    /// </summary>
    private static async ValueTask<(
        OkResult? Ok,
        List<ColumnDefinition>? Columns
    )> ReadResultSetHeaderAsync(
        byte[] firstPayload,
        PacketReader reader,
        uint? connectionId,
        bool okTerminatesColumns,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(firstPayload);
        ArgumentNullException.ThrowIfNull(reader);

        if (firstPayload.Length == 0)
        {
            throw new MalformedPacketException("Empty query response packet.");
        }

        if (ResultPacket.IsError(firstPayload))
        {
            throw ResultPacket.ReadError(firstPayload, connectionId);
        }

        if (ResultPacket.IsOk(firstPayload))
        {
            return (ResultPacket.ReadOk(firstPayload), null);
        }

        if (firstPayload[0] == ResultPacket.LocalInFileHeader)
        {
            throw new DotRocksException(
                "StarRocks requested LOCAL INFILE, which DotRocks does not support."
            );
        }

        int columnCount = ReadColumnCount(firstPayload);
        var columns = new List<ColumnDefinition>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            byte[] columnPayload = await reader
                .ReadPayloadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (ResultPacket.IsError(columnPayload))
            {
                throw ResultPacket.ReadError(columnPayload, connectionId);
            }

            columns.Add(ReadColumnDefinition(columnPayload));
        }

        byte[] columnsTerminator = await reader
            .ReadPayloadAsync(cancellationToken)
            .ConfigureAwait(false);
        bool terminated =
            ResultPacket.IsEndOfResultSet(columnsTerminator)
            || (okTerminatesColumns && ResultPacket.IsOk(columnsTerminator));
        if (!terminated)
        {
            throw new MalformedPacketException(
                okTerminatesColumns
                    ? "Expected an EOF or OK packet after column definitions."
                    : "Expected an EOF packet after the prepared-statement column definitions."
            );
        }

        return (null, columns);
    }

    private static (OkResult? Ok, List<ColumnDefinition>? Columns) ReadResultSetHeader(
        byte[] firstPayload,
        PacketReader reader,
        uint? connectionId,
        bool okTerminatesColumns
    )
    {
        ArgumentNullException.ThrowIfNull(firstPayload);
        ArgumentNullException.ThrowIfNull(reader);

        if (firstPayload.Length == 0)
        {
            throw new MalformedPacketException("Empty query response packet.");
        }

        if (ResultPacket.IsError(firstPayload))
        {
            throw ResultPacket.ReadError(firstPayload, connectionId);
        }

        if (ResultPacket.IsOk(firstPayload))
        {
            return (ResultPacket.ReadOk(firstPayload), null);
        }

        if (firstPayload[0] == ResultPacket.LocalInFileHeader)
        {
            throw new DotRocksException(
                "StarRocks requested LOCAL INFILE, which DotRocks does not support."
            );
        }

        int columnCount = ReadColumnCount(firstPayload);
        var columns = new List<ColumnDefinition>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            byte[] columnPayload = reader.ReadPayload();
            if (ResultPacket.IsError(columnPayload))
            {
                throw ResultPacket.ReadError(columnPayload, connectionId);
            }

            columns.Add(ReadColumnDefinition(columnPayload));
        }

        byte[] columnsTerminator = reader.ReadPayload();
        bool terminated =
            ResultPacket.IsEndOfResultSet(columnsTerminator)
            || (okTerminatesColumns && ResultPacket.IsOk(columnsTerminator));
        if (!terminated)
        {
            throw new MalformedPacketException(
                okTerminatesColumns
                    ? "Expected an EOF or OK packet after column definitions."
                    : "Expected an EOF packet after the prepared-statement column definitions."
            );
        }

        return (null, columns);
    }

    private static async ValueTask<List<object?[]>> ReadAllRowsAsync(
        ResultRowReader rowReader,
        CancellationToken cancellationToken
    )
    {
        var rows = new List<object?[]>();
        while (await rowReader.ReadRowAsync(cancellationToken).ConfigureAwait(false) is { } row)
        {
            rows.Add(row);
        }

        return rows;
    }

    internal static int ReadColumnCount(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtocolReader(payload);
        ulong value = reader.ReadLengthEncodedInteger();
        if (value > MaxColumnCount)
        {
            throw new MalformedPacketException(
                $"Result-set column count {value} exceeds the supported maximum of {MaxColumnCount}."
            );
        }

        return (int)value;
    }

    internal static ColumnDefinition ReadColumnDefinition(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtocolReader(payload);
        string catalog = ReadLengthEncodedString(ref reader);
        string schema = ReadLengthEncodedString(ref reader);
        string table = ReadLengthEncodedString(ref reader);
        string originalTable = ReadLengthEncodedString(ref reader);
        string name = ReadLengthEncodedString(ref reader);
        string originalName = ReadLengthEncodedString(ref reader);
        ulong fixedLength = reader.ReadLengthEncodedInteger();
        if (fixedLength != 0x0C)
        {
            throw new MalformedPacketException(
                $"Unsupported column-definition fixed field length {fixedLength}."
            );
        }

        ushort characterSet = (ushort)reader.ReadFixedInteger(2);
        uint columnLength = (uint)reader.ReadFixedInteger(4);
        byte columnType = reader.ReadByte();
        ushort flags = (ushort)reader.ReadFixedInteger(2);
        byte decimals = reader.ReadByte();
        _ = reader.ReadFixedInteger(2);
        if (!reader.IsAtEnd)
        {
            throw new MalformedPacketException("Column definition contains trailing bytes.");
        }

        return new ColumnDefinition(
            catalog,
            schema,
            table,
            originalTable,
            name,
            originalName,
            characterSet,
            columnLength,
            columnType,
            flags,
            decimals
        );
    }

    internal static object?[] ReadTextRow(
        ReadOnlySpan<byte> payload,
        IReadOnlyList<ColumnDefinition> columns
    )
    {
        ArgumentNullException.ThrowIfNull(columns);

        var reader = new ProtocolReader(payload);
        var values = new object?[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            ReadOnlySpan<byte> bytes = reader.ReadLengthEncodedBytes(out bool isNull);
            values[i] = isNull
                ? null
                : ColumnTypeMapper.ParseTextValue(
                    columns[i].ColumnType,
                    columns[i].ColumnLength,
                    bytes
                );
        }

        if (!reader.IsAtEnd)
        {
            throw new MalformedPacketException("Text row contains trailing bytes.");
        }

        return values;
    }

    private static string ReadLengthEncodedString(ref ProtocolReader reader) =>
        reader.ReadLengthEncodedString(Encoding.UTF8, out bool isNull)
        ?? throw new MalformedPacketException(
            "Column definition contained NULL where a string was required."
        );
}
