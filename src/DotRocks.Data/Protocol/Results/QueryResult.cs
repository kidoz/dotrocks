namespace DotRocks.Data.Protocol.Results;

internal sealed class QueryResult
{
    private QueryResult(
        IReadOnlyList<ColumnDefinition> columns,
        IReadOnlyList<object?[]> rows,
        long recordsAffected
    )
    {
        Columns = columns;
        Rows = rows;
        RecordsAffected = recordsAffected;
    }

    public IReadOnlyList<ColumnDefinition> Columns { get; }

    public IReadOnlyList<object?[]> Rows { get; }

    public long RecordsAffected { get; }

    public bool HasResultSet => Columns.Count > 0;

    public static QueryResult FromRows(
        IReadOnlyList<ColumnDefinition> columns,
        IReadOnlyList<object?[]> rows
    ) => new(columns, rows, recordsAffected: -1);

    public static QueryResult FromOk(OkResult ok) => new([], [], ok.AffectedRows);

    /// <summary>
    /// Aggregates <see cref="RecordsAffected"/> across results using the ADO.NET convention:
    /// results reporting -1 (no DML count) are skipped, and the total stays -1 when no result
    /// reported an affected-row count.
    /// </summary>
    public static long SumRecordsAffected(IReadOnlyList<QueryResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        long total = -1;
        foreach (QueryResult result in results)
        {
            if (result.RecordsAffected >= 0)
            {
                total = (total < 0 ? 0 : total) + result.RecordsAffected;
            }
        }

        return total;
    }
}
