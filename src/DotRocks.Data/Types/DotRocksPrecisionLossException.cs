namespace DotRocks.Data;

/// <summary>
/// Represents a loss of precision while converting a DotRocks value to a narrower CLR type.
/// </summary>
public sealed class DotRocksPrecisionLossException : DotRocksException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksPrecisionLossException"/> class.
    /// </summary>
    public DotRocksPrecisionLossException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksPrecisionLossException"/> class with a
    /// message.
    /// </summary>
    /// <param name="message">The sanitized error message.</param>
    public DotRocksPrecisionLossException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksPrecisionLossException"/> class with a
    /// message and inner exception.
    /// </summary>
    /// <param name="message">The sanitized error message.</param>
    /// <param name="innerException">The underlying non-secret exception.</param>
    public DotRocksPrecisionLossException(string message, Exception innerException)
        : base(message, innerException) { }
}
