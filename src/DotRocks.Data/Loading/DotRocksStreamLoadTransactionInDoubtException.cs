namespace DotRocks.Data.Loading;

/// <summary>
/// Represents a Stream Load transaction completion whose server-side outcome is unknown.
/// </summary>
public sealed class DotRocksStreamLoadTransactionInDoubtException : DotRocksException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksStreamLoadTransactionInDoubtException"/> class.
    /// </summary>
    public DotRocksStreamLoadTransactionInDoubtException()
    {
        Label = string.Empty;
        Operation = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksStreamLoadTransactionInDoubtException"/> class with a
    /// message.
    /// </summary>
    /// <param name="message">The sanitized error message.</param>
    public DotRocksStreamLoadTransactionInDoubtException(string message)
        : base(message)
    {
        Label = string.Empty;
        Operation = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksStreamLoadTransactionInDoubtException"/> class with a
    /// message and inner exception.
    /// </summary>
    /// <param name="message">The sanitized error message.</param>
    /// <param name="innerException">The underlying non-secret exception.</param>
    public DotRocksStreamLoadTransactionInDoubtException(string message, Exception innerException)
        : base(message, innerException)
    {
        Label = string.Empty;
        Operation = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksStreamLoadTransactionInDoubtException"/> class.
    /// </summary>
    /// <param name="label">The Stream Load transaction label.</param>
    /// <param name="operation">The transaction completion operation.</param>
    /// <param name="innerException">The underlying cancellation or I/O exception.</param>
    public DotRocksStreamLoadTransactionInDoubtException(
        string label,
        string operation,
        Exception innerException
    )
        : base(
            $"The StarRocks Stream Load transaction '{label}' {operation} request was sent, but the outcome is unknown.",
            innerException
        )
    {
        Label = label;
        Operation = operation;
    }

    /// <summary>
    /// Gets the Stream Load transaction label.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the transaction completion operation.
    /// </summary>
    public string Operation { get; }
}
