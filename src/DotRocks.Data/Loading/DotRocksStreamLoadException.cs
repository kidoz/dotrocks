using System.Net;

namespace DotRocks.Data.Loading;

/// <summary>
/// Represents a StarRocks Stream Load failure.
/// </summary>
public sealed class DotRocksStreamLoadException : DotRocksException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksStreamLoadException"/> class.
    /// </summary>
    public DotRocksStreamLoadException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksStreamLoadException"/> class with a
    /// message.
    /// </summary>
    /// <param name="message">The sanitized error message.</param>
    public DotRocksStreamLoadException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksStreamLoadException"/> class with a
    /// message and inner exception.
    /// </summary>
    /// <param name="message">The sanitized error message.</param>
    /// <param name="innerException">The underlying non-secret exception.</param>
    public DotRocksStreamLoadException(string message, Exception innerException)
        : base(message, innerException) { }

    internal DotRocksStreamLoadException(
        string message,
        HttpStatusCode? httpStatusCode,
        DotRocksStreamLoadResult? result,
        string? responseBody = null
    )
        : base(message)
    {
        HttpStatusCode = httpStatusCode;
        Result = result;
        ResponseBody = responseBody;
    }

    /// <summary>
    /// Gets the HTTP status code, when the request reached the Stream Load endpoint.
    /// </summary>
    public HttpStatusCode? HttpStatusCode { get; }

    /// <summary>
    /// Gets the parsed Stream Load result, when StarRocks returned one.
    /// </summary>
    public DotRocksStreamLoadResult? Result { get; }

    /// <summary>
    /// Gets the raw HTTP response body for a non-success status, when one was available. Like
    /// <see cref="Result"/>, this is deliberately a structured property rather than part of the
    /// exception message: server text is untrusted and may carry row data, so it must not flow
    /// into logs implicitly via <see cref="Exception.ToString"/>.
    /// </summary>
    public string? ResponseBody { get; }
}
