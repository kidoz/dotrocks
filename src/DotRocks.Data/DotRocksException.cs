namespace DotRocks.Data;

/// <summary>
/// Represents a sanitized DotRocks driver error.
/// </summary>
public class DotRocksException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksException"/> class.
    /// </summary>
    public DotRocksException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksException"/> class with a message.
    /// </summary>
    /// <param name="message">The sanitized error message.</param>
    public DotRocksException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksException"/> class with a message and
    /// inner exception.
    /// </summary>
    /// <param name="message">The sanitized error message.</param>
    /// <param name="innerException">The underlying non-secret exception.</param>
    public DotRocksException(string message, Exception innerException)
        : base(message, innerException) { }

    internal DotRocksException(
        string message,
        int? serverErrorCode,
        string? sqlState,
        bool isTransient,
        uint? connectionId,
        Exception? innerException = null
    )
        : base(message, innerException)
    {
        ServerErrorCode = serverErrorCode;
        SqlState = sqlState;
        IsTransient = isTransient;
        ConnectionId = connectionId;
    }

    /// <summary>
    /// Gets the StarRocks server error code, when one was available.
    /// </summary>
    public int? ServerErrorCode { get; }

    /// <summary>
    /// Gets the SQLSTATE value reported by StarRocks, when one was available.
    /// </summary>
    public string? SqlState { get; }

    /// <summary>
    /// Gets a value indicating whether retrying the operation may succeed.
    /// </summary>
    public bool IsTransient { get; }

    /// <summary>
    /// Gets the non-secret server connection identifier, when one was available.
    /// </summary>
    public uint? ConnectionId { get; }
}
