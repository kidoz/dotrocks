using System.Buffers;

namespace DotRocks.Data.Protocol.Framing;

/// <summary>
/// A protocol payload held in a buffer that may be rented from <see cref="ArrayPool{T}"/>. The
/// buffer is usually larger than the payload, so consumers must read through <see cref="Span"/>
/// rather than the array's own length, and must dispose the payload once decoding finishes.
/// </summary>
/// <remarks>
/// This exists for the result-row hot path, where allocating a fresh array per row dominated the
/// read path's allocation. Renting is only safe because every decoded value copies out of the
/// payload (strings and byte arrays are copied; numerics and dates parse to value types), so no
/// materialized value can observe the buffer after it returns to the pool.
/// </remarks>
internal readonly struct PooledPayload : IDisposable
{
    private readonly byte[] _buffer;
    private readonly bool _pooled;

    internal PooledPayload(byte[] buffer, int length, bool pooled)
    {
        _buffer = buffer;
        _pooled = pooled;
        Length = length;
    }

    /// <summary>The payload length, which may be shorter than the underlying buffer.</summary>
    public int Length { get; }

    /// <summary>The payload bytes.</summary>
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);

    public void Dispose()
    {
        if (_pooled)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}
