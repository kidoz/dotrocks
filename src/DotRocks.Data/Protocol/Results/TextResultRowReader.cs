using DotRocks.Data.Protocol.Framing;

namespace DotRocks.Data.Protocol.Results;

internal sealed class TextResultRowReader
{
    private readonly PacketReader _reader;
    private readonly uint? _connectionId;
    private bool _isConsumed;

    public TextResultRowReader(
        PacketReader reader,
        IReadOnlyList<ColumnDefinition> columns,
        uint? connectionId
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(columns);
        _reader = reader;
        Columns = columns;
        _connectionId = connectionId;
    }

    public IReadOnlyList<ColumnDefinition> Columns { get; }

    public bool IsConsumed => _isConsumed;

    public async ValueTask<object?[]?> ReadRowAsync(CancellationToken cancellationToken)
    {
        if (_isConsumed)
        {
            return null;
        }

        byte[] rowPayload = await _reader.ReadPayloadAsync(cancellationToken).ConfigureAwait(false);
        if (ResultPacket.IsError(rowPayload))
        {
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

        return TextResultParser.ReadTextRow(rowPayload, Columns);
    }
}
