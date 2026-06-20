using System.Text;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Results;

internal static class TextResultParser
{
    public static async ValueTask<QueryResult> ReadAsync(
        byte[] firstPayload,
        PacketReader reader,
        uint? connectionId,
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
            return QueryResult.FromOk(ResultPacket.ReadOk(firstPayload));
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
        if (
            !ResultPacket.IsEndOfResultSet(columnsTerminator)
            && !ResultPacket.IsOk(columnsTerminator)
        )
        {
            throw new MalformedPacketException(
                "Expected an EOF or OK packet after column definitions."
            );
        }

        var rows = new List<object?[]>();
        while (true)
        {
            byte[] rowPayload = await reader
                .ReadPayloadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (ResultPacket.IsError(rowPayload))
            {
                throw ResultPacket.ReadError(rowPayload, connectionId);
            }

            // Rows are terminated by an EOF packet (the client negotiates EOF, not
            // DEPRECATE_EOF). A 0x00 first byte here is a row whose first column is an empty
            // string, not an OK terminator — treating it as OK would silently truncate results.
            if (ResultPacket.IsEndOfResultSet(rowPayload))
            {
                break;
            }

            rows.Add(ReadTextRow(rowPayload, columns));
        }

        return QueryResult.FromRows(columns, rows);
    }

    public static async ValueTask<StreamingQueryResult> ReadStreamingAsync(
        byte[] firstPayload,
        PacketReader reader,
        uint? connectionId,
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
            return StreamingQueryResult.FromOk(ResultPacket.ReadOk(firstPayload));
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
        if (
            !ResultPacket.IsEndOfResultSet(columnsTerminator)
            && !ResultPacket.IsOk(columnsTerminator)
        )
        {
            throw new MalformedPacketException(
                "Expected an EOF or OK packet after column definitions."
            );
        }

        return StreamingQueryResult.FromRows(
            columns,
            new TextResultRowReader(reader, columns, connectionId)
        );
    }

    internal static int ReadColumnCount(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtocolReader(payload);
        ulong value = reader.ReadLengthEncodedInteger();
        if (value > int.MaxValue)
        {
            throw new MalformedPacketException("Result-set column count exceeds Int32.MaxValue.");
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
                : ColumnTypeMapper.ParseTextValue(columns[i].ColumnType, bytes);
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
