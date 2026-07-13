using DotRocks.Data.Protocol.Framing;

namespace DotRocks.Data.Protocol.Results;

/// <summary>
/// Decodes one result-set row payload. The text and binary protocols share the packet-level row
/// loop and differ only in this row encoding.
/// </summary>
internal delegate object?[] ResultRowDecoder(
    byte[] rowPayload,
    IReadOnlyList<ColumnDefinition> columns
);

/// <summary>
/// Reads result-set rows packet by packet until the terminating EOF packet, surfacing an ERR
/// packet sent in place of a row (a server that aborts the query mid-stream — timeout, kill —
/// reports the real server error that way) as the corresponding exception. This is the single
/// home for the row-loop packet rules; create instances via <see cref="ForText"/> or
/// <see cref="ForBinary"/> to select the row encoding.
/// </summary>
internal sealed class ResultRowReader
{
    private readonly PacketReader _reader;
    private readonly uint? _connectionId;
    private readonly ResultRowDecoder _decodeRow;
    private bool _isConsumed;

    private ResultRowReader(
        PacketReader reader,
        IReadOnlyList<ColumnDefinition> columns,
        uint? connectionId,
        ResultRowDecoder decodeRow
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(columns);
        _reader = reader;
        Columns = columns;
        _connectionId = connectionId;
        _decodeRow = decodeRow;
    }

    /// <summary>Creates a reader for text-protocol rows (<c>COM_QUERY</c> responses).</summary>
    public static ResultRowReader ForText(
        PacketReader reader,
        IReadOnlyList<ColumnDefinition> columns,
        uint? connectionId
    ) =>
        new(
            reader,
            columns,
            connectionId,
            static (payload, rowColumns) => TextResultParser.ReadTextRow(payload, rowColumns)
        );

    /// <summary>Creates a reader for binary-protocol rows (<c>COM_STMT_EXECUTE</c> responses).</summary>
    public static ResultRowReader ForBinary(
        PacketReader reader,
        IReadOnlyList<ColumnDefinition> columns,
        uint? connectionId
    ) =>
        new(
            reader,
            columns,
            connectionId,
            static (payload, rowColumns) => BinaryResultRowDecoder.Decode(payload, rowColumns)
        );

    public IReadOnlyList<ColumnDefinition> Columns { get; }

    public bool IsConsumed => _isConsumed;

    public async ValueTask<object?[]?> ReadRowAsync(CancellationToken cancellationToken)
    {
        if (_isConsumed)
        {
            return null;
        }

        byte[] rowPayload = await _reader.ReadPayloadAsync(cancellationToken).ConfigureAwait(false);
        return DecodeRowPayload(rowPayload);
    }

    public object?[]? ReadRow()
    {
        if (_isConsumed)
        {
            return null;
        }

        return DecodeRowPayload(_reader.ReadPayload());
    }

    /// <summary>
    /// Consumes the remaining row packets without decoding their values. This keeps the connection
    /// at a clean packet boundary when a caller abandons a result while avoiding allocations for
    /// values that will never be observed.
    /// </summary>
    public async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        while (!_isConsumed)
        {
            byte[] rowPayload = await _reader
                .ReadPayloadAsync(cancellationToken)
                .ConfigureAwait(false);
            ProcessDrainedPayload(rowPayload);
        }
    }

    public void Drain()
    {
        while (!_isConsumed)
        {
            ProcessDrainedPayload(_reader.ReadPayload());
        }
    }

    private object?[]? DecodeRowPayload(byte[] rowPayload)
    {
        if (ResultPacket.IsError(rowPayload))
        {
            _isConsumed = true;
            throw ResultPacket.ReadError(rowPayload, _connectionId);
        }

        // Rows are terminated by an EOF packet only. A 0x00 first byte is a row whose first
        // column is an empty string, not an OK terminator; treating it as a terminator would
        // silently truncate the result set.
        if (ResultPacket.IsEndOfResultSet(rowPayload))
        {
            _isConsumed = true;
            return null;
        }

        return _decodeRow(rowPayload, Columns);
    }

    private void ProcessDrainedPayload(byte[] rowPayload)
    {
        if (ResultPacket.IsError(rowPayload))
        {
            _isConsumed = true;
            throw ResultPacket.ReadError(rowPayload, _connectionId);
        }

        if (ResultPacket.IsEndOfResultSet(rowPayload))
        {
            _isConsumed = true;
        }
    }
}
