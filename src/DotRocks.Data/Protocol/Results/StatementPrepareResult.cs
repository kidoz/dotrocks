namespace DotRocks.Data.Protocol.Results;

/// <summary>
/// The result of a <c>COM_STMT_PREPARE</c>: the server-assigned statement id and the parameter and
/// column counts the server reported for the prepared statement.
/// </summary>
internal readonly record struct StatementPrepareResult(
    uint StatementId,
    int ParameterCount,
    int ColumnCount
);
