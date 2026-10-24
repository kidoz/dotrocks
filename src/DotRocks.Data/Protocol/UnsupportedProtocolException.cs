namespace DotRocks.Data.Protocol;

/// <summary>
/// The server requested an unsupported protocol exchange, leaving the session unusable.
/// </summary>
internal sealed class UnsupportedProtocolException : DotRocksException
{
    public UnsupportedProtocolException() { }

    public UnsupportedProtocolException(string message)
        : base(message) { }

    public UnsupportedProtocolException(string message, Exception innerException)
        : base(message, innerException) { }
}
