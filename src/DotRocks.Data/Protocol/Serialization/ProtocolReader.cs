using System.Text;

namespace DotRocks.Data.Protocol.Serialization;

/// <summary>
/// Forward-only reader over a single StarRocks protocol payload. Every read is bounds-checked and
/// raises <see cref="MalformedPacketException"/> rather than reading past the end of the buffer, so
/// truncated or hostile packets fail loudly instead of corrupting state or over-reading memory.
/// </summary>
internal ref struct ProtocolReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public ProtocolReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>Number of bytes already consumed.</summary>
    public readonly int Position => _position;

    /// <summary>Number of bytes left to read.</summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>True when no bytes remain.</summary>
    public readonly bool IsAtEnd => _position >= _buffer.Length;

    public byte ReadByte()
    {
        if (_position >= _buffer.Length)
        {
            throw new MalformedPacketException("Unexpected end of packet while reading a byte.");
        }

        return _buffer[_position++];
    }

    public readonly byte PeekByte()
    {
        if (_position >= _buffer.Length)
        {
            throw new MalformedPacketException("Unexpected end of packet while reading a byte.");
        }

        return _buffer[_position];
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > Remaining)
        {
            throw new MalformedPacketException(
                $"Packet truncated: requested {count} byte(s) but only {Remaining} remain."
            );
        }

        ReadOnlySpan<byte> slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    /// <summary>Reads a little-endian fixed-width unsigned integer of 1 to 8 bytes.</summary>
    public ulong ReadFixedInteger(int byteCount)
    {
        if (byteCount is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCount),
                byteCount,
                "Fixed-width integers are 1 to 8 bytes."
            );
        }

        if (byteCount > Remaining)
        {
            throw new MalformedPacketException(
                $"Packet truncated: requested {byteCount}-byte integer but only {Remaining} remain."
            );
        }

        ulong value = 0;
        for (int i = 0; i < byteCount; i++)
        {
            value |= (ulong)_buffer[_position + i] << (8 * i);
        }

        _position += byteCount;
        return value;
    }

    /// <summary>
    /// Reads a length-encoded integer. <paramref name="isNull"/> is set when the value is the NULL
    /// marker (0xFB), which is only meaningful when reading result-row values.
    /// </summary>
    public ulong ReadLengthEncodedInteger(out bool isNull)
    {
        isNull = false;
        byte first = ReadByte();
        switch (first)
        {
            case < ProtocolConstants.LengthEncodedOneByteLimit:
                return first;
            case ProtocolConstants.NullValueMarker:
                isNull = true;
                return 0;
            case ProtocolConstants.LengthEncodedTwoBytePrefix:
                return ReadFixedInteger(2);
            case ProtocolConstants.LengthEncodedThreeBytePrefix:
                return ReadFixedInteger(3);
            case ProtocolConstants.LengthEncodedEightBytePrefix:
                return ReadFixedInteger(8);
            default:
                throw new MalformedPacketException(
                    $"Invalid length-encoded integer prefix 0x{first:X2}."
                );
        }
    }

    /// <summary>Reads a length-encoded integer that must not be NULL.</summary>
    public ulong ReadLengthEncodedInteger()
    {
        ulong value = ReadLengthEncodedInteger(out bool isNull);
        if (isNull)
        {
            throw new MalformedPacketException(
                "Encountered a NULL marker where a length-encoded integer was required."
            );
        }

        return value;
    }

    /// <summary>Reads a length-encoded byte string. Returns an empty span when the value is NULL.</summary>
    public ReadOnlySpan<byte> ReadLengthEncodedBytes(out bool isNull)
    {
        ulong length = ReadLengthEncodedInteger(out isNull);
        if (isNull)
        {
            return default;
        }

        if (length > (ulong)Remaining)
        {
            throw new MalformedPacketException(
                $"Packet truncated: length-encoded value claims {length} byte(s) but only {Remaining} remain."
            );
        }

        return ReadBytes((int)length);
    }

    /// <summary>Reads a length-encoded string. Returns <c>null</c> when the value is NULL.</summary>
    public string? ReadLengthEncodedString(Encoding encoding, out bool isNull)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        ReadOnlySpan<byte> bytes = ReadLengthEncodedBytes(out isNull);
        return isNull ? null : encoding.GetString(bytes);
    }

    /// <summary>Reads a NUL-terminated string and consumes the terminator.</summary>
    public string ReadNullTerminatedString(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        int terminator = _buffer[_position..].IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new MalformedPacketException("Unterminated NUL-terminated string.");
        }

        ReadOnlySpan<byte> slice = _buffer.Slice(_position, terminator);
        _position += terminator + 1;
        return encoding.GetString(slice);
    }

    /// <summary>Consumes and returns the remainder of the buffer.</summary>
    public ReadOnlySpan<byte> ReadToEnd()
    {
        ReadOnlySpan<byte> slice = _buffer[_position..];
        _position = _buffer.Length;
        return slice;
    }
}
