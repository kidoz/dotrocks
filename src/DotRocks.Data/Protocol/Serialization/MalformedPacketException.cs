using System.Diagnostics.CodeAnalysis;

namespace DotRocks.Data.Protocol.Serialization;

/// <summary>
/// Thrown when the wire bytes received from (or being written for) StarRocks do not form a
/// well-formed protocol element. This is an internal signal; the public surface wraps it in a
/// <c>DotRocksProtocolException</c> once the public exception model exists.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1064:Exceptions should be public",
    Justification = "MalformedPacketException is an internal parser signal wrapped by public DotRocksException."
)]
internal sealed class MalformedPacketException : Exception
{
    public MalformedPacketException() { }

    public MalformedPacketException(string message)
        : base(message) { }

    public MalformedPacketException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>
    /// True when the payload was well-formed but larger than the client's configured limit,
    /// rather than corrupt. The two failures need different messages: an oversized result is a
    /// client limit the caller can act on, not evidence that StarRocks sent malformed bytes.
    /// </summary>
    public bool IsPayloadTooLarge { get; init; }
}
