namespace DotRocks.Data;

/// <summary>
/// Thrown when DotRocks is asked to use a capability it does not implement or that has not been
/// verified against the targeted StarRocks version, such as the server-side prepared-statement
/// protocol. The failure is explicit so callers never silently fall back to a different mechanism.
/// </summary>
public sealed class DotRocksUnsupportedFeatureException : DotRocksException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksUnsupportedFeatureException"/> class.
    /// </summary>
    public DotRocksUnsupportedFeatureException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksUnsupportedFeatureException"/> class
    /// with a message.
    /// </summary>
    /// <param name="message">The error message describing the unsupported feature.</param>
    public DotRocksUnsupportedFeatureException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksUnsupportedFeatureException"/> class
    /// with a message and inner exception.
    /// </summary>
    /// <param name="message">The error message describing the unsupported feature.</param>
    /// <param name="innerException">The underlying non-secret exception.</param>
    public DotRocksUnsupportedFeatureException(string message, Exception innerException)
        : base(message, innerException) { }
}
