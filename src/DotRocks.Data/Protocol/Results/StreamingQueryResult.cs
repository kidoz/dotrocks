namespace DotRocks.Data.Protocol.Results;

internal sealed class StreamingQueryResult
{
    private StreamingQueryResult(
        IReadOnlyList<ColumnDefinition> columns,
        ResultRowReader? rowReader,
        long recordsAffected
    )
    {
        Columns = columns;
        RowReader = rowReader;
        RecordsAffected = recordsAffected;
    }

    public IReadOnlyList<ColumnDefinition> Columns { get; }

    public ResultRowReader? RowReader { get; }

    public long RecordsAffected { get; }

    public bool HasResultSet => Columns.Count > 0;

    public static StreamingQueryResult FromRows(
        IReadOnlyList<ColumnDefinition> columns,
        ResultRowReader rowReader
    ) => new(columns, rowReader, recordsAffected: -1);

    public static StreamingQueryResult FromOk(OkResult ok) => new([], null, ok.AffectedRows);
}
