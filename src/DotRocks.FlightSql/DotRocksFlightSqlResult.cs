using Apache.Arrow;

namespace DotRocks.FlightSql;

/// <summary>
/// Describes a Flight SQL query result and provides its streamed Arrow record batches.
/// </summary>
public sealed class DotRocksFlightSqlResult
{
    private readonly Func<CancellationToken, IAsyncEnumerable<RecordBatch>> _readBatches;
    private int _readStarted;

    internal DotRocksFlightSqlResult(
        Schema? schema,
        long totalRecords,
        long totalBytes,
        bool isOrdered,
        Func<CancellationToken, IAsyncEnumerable<RecordBatch>> readBatches
    )
    {
        Schema = schema;
        TotalRecords = totalRecords < 0 ? null : totalRecords;
        TotalBytes = totalBytes < 0 ? null : totalBytes;
        IsOrdered = isOrdered;
        _readBatches = readBatches;
    }

    /// <summary>
    /// Gets the result schema, or <see langword="null" /> when the server omitted it.
    /// </summary>
    public Schema? Schema { get; }

    /// <summary>
    /// Gets the total record count reported by the server, or <see langword="null" /> when unknown.
    /// </summary>
    public long? TotalRecords { get; }

    /// <summary>
    /// Gets the total byte count reported by the server, or <see langword="null" /> when unknown.
    /// </summary>
    public long? TotalBytes { get; }

    /// <summary>
    /// Gets whether the server declares the result endpoints to be ordered.
    /// </summary>
    public bool IsOrdered { get; }

    /// <summary>
    /// Reads the query result as Arrow record batches without row materialization.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels result streaming.</param>
    /// <returns>The single-use asynchronous record-batch stream.</returns>
    public IAsyncEnumerable<RecordBatch> ReadRecordBatchesAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (Interlocked.Exchange(ref _readStarted, 1) != 0)
        {
            throw new InvalidOperationException("A Flight SQL result can be read only once.");
        }

        return _readBatches(cancellationToken);
    }
}
