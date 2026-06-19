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
}
