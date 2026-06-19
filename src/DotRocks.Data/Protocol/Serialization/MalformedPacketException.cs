namespace DotRocks.Data.Protocol.Serialization;

/// <summary>
/// Thrown when the wire bytes received from (or being written for) StarRocks do not form a
/// well-formed protocol element. This is an internal signal; the public surface wraps it in a
/// <c>DotRocksProtocolException</c> once the public exception model exists.
/// </summary>
internal sealed class MalformedPacketException : Exception
{
    public MalformedPacketException() { }

    public MalformedPacketException(string message)
        : base(message) { }

    public MalformedPacketException(string message, Exception innerException)
        : base(message, innerException) { }
}
