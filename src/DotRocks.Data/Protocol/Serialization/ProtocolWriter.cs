using System.Buffers;
using System.Text;

namespace DotRocks.Data.Protocol.Serialization;

/// <summary>
/// Growable writer for building a StarRocks protocol payload. Backing storage is rented from
/// <see cref="ArrayPool{T}"/> and cleared on return so credential and parameter bytes are not left
/// behind in pooled memory. Dispose when finished.
/// </summary>
internal sealed class ProtocolWriter : IDisposable
{
    private const int MinimumCapacity = 16;

    private byte[]? _buffer;
    private int _position;

    public ProtocolWriter(int initialCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, MinimumCapacity));
        _position = 0;
    }

    /// <summary>Number of bytes written so far.</summary>
    public int Length => _position;

    /// <summary>The bytes written so far. Valid until the next write or <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> WrittenSpan => Storage.AsSpan(0, _position);

    private byte[] Storage => _buffer ?? throw new ObjectDisposedException(nameof(ProtocolWriter));

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        Storage[_position++] = value;
    }

    /// <summary>Writes a little-endian fixed-width unsigned integer of 1 to 8 bytes.</summary>
    public void WriteFixedInteger(ulong value, int byteCount)
    {
        if ((uint)byteCount > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCount),
                byteCount,
                "Fixed-width integers are 0 to 8 bytes."
            );
        }

        EnsureCapacity(byteCount);
        for (int i = 0; i < byteCount; i++)
        {
            Storage[_position + i] = (byte)(value >> (8 * i));
        }

        _position += byteCount;
    }

    /// <summary>Writes a length-encoded integer using the smallest legal encoding.</summary>
    public void WriteLengthEncodedInteger(ulong value)
    {
        if (value < ProtocolConstants.LengthEncodedOneByteLimit)
        {
            WriteByte((byte)value);
        }
        else if (value <= 0xFFFF)
        {
            WriteByte(ProtocolConstants.LengthEncodedTwoBytePrefix);
            WriteFixedInteger(value, 2);
        }
        else if (value <= 0xFFFFFF)
        {
            WriteByte(ProtocolConstants.LengthEncodedThreeBytePrefix);
            WriteFixedInteger(value, 3);
        }
        else
        {
            WriteByte(ProtocolConstants.LengthEncodedEightBytePrefix);
            WriteFixedInteger(value, 8);
        }
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(Storage.AsSpan(_position));
        _position += bytes.Length;
    }

    public void WriteLengthEncodedBytes(ReadOnlySpan<byte> bytes)
    {
        WriteLengthEncodedInteger((ulong)bytes.Length);
        WriteBytes(bytes);
    }

    public void WriteLengthEncodedString(string value, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(encoding);
        int byteCount = encoding.GetByteCount(value);
        WriteLengthEncodedInteger((ulong)byteCount);
        EnsureCapacity(byteCount);
        int written = encoding.GetBytes(value, Storage.AsSpan(_position));
        _position += written;
    }

    public void WriteNullTerminatedString(string value, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(encoding);
        int byteCount = encoding.GetByteCount(value);
        EnsureCapacity(byteCount + 1);
        int written = encoding.GetBytes(value, Storage.AsSpan(_position));
        _position += written;
        WriteByte(0);
    }

    /// <summary>Copies the written bytes into a freshly allocated array.</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();

    private void EnsureCapacity(int additional)
    {
        byte[] current = Storage;
        int required = _position + additional;
        if (required <= current.Length)
        {
            return;
        }

        int newSize = Math.Max(required, current.Length * 2);
        byte[] grown = ArrayPool<byte>.Shared.Rent(newSize);
        Array.Copy(current, grown, _position);
        ArrayPool<byte>.Shared.Return(current, clearArray: true);
        _buffer = grown;
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = null;
            _position = 0;
        }
    }
}
