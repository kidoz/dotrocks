using System.Diagnostics;
using System.Globalization;

namespace DotRocks.Data.Diagnostics;

/// <summary>
/// Safe span-tagging helpers shared by connection, command, and future instrumentation. Telemetry
/// is a public contract and a security surface: these helpers never accept or emit raw SQL,
/// parameter values, connection strings, passwords, usernames, or server message text, and they
/// avoid work when no listener is sampling the activity.
/// </summary>
internal static class DotRocksTelemetryTags
{
    // OpenTelemetry database semantic-convention attribute names.
    public const string DbSystemName = "db.system.name";
    public const string DbOperationName = "db.operation.name";
    public const string DbQuerySummary = "db.query.summary";
    public const string DbNamespace = "db.namespace";
    public const string ServerPort = "server.port";
    public const string DbResponseStatusCode = "db.response.status_code";
    public const string ErrorType = "error.type";

    // db.system.name value: 'other_sql' until a stable 'starrocks' registry value exists.
    public const string DbSystemValue = "other_sql";

    // Stable, low-cardinality error classifications (also used as metric outcome values).
    public const string ErrorTimeout = "timeout";
    public const string ErrorCanceled = "canceled";

    // Bounded metric outcome label values.
    public const string OutcomeSuccess = "success";
    public const string OutcomeError = "error";

    private static readonly string[] KnownOperations =
    [
        "SELECT",
        "INSERT",
        "UPDATE",
        "DELETE",
        "SET",
        "USE",
        "CREATE",
        "DROP",
        "ALTER",
        "SHOW",
        "WITH",
        "EXPLAIN",
        "TRUNCATE",
        "GRANT",
        "REVOKE",
        "BEGIN",
        "COMMIT",
        "ROLLBACK",
    ];

    /// <summary>Tags a connection-open span with safe, non-tenant-bearing attributes.</summary>
    public static void TagConnectionOpen(Activity? activity, DotRocksConnectionOptions options)
    {
        if (activity is null || !activity.IsAllDataRequested)
        {
            return;
        }

        activity.SetTag(DbSystemName, DbSystemValue);
        activity.SetTag(ServerPort, options.Port);
        if (!string.IsNullOrEmpty(options.Database))
        {
            activity.SetTag(DbNamespace, options.Database);
        }

        // server.address is omitted by default: a configured host may be tenant-bearing and there
        // is no sensitivity marking to clear it. A future opt-in can add it explicitly.
    }

    /// <summary>Tags a command span with the safe operation name and a literal-free summary.</summary>
    public static void TagCommandStart(Activity? activity, string commandText)
    {
        if (activity is null || !activity.IsAllDataRequested)
        {
            return;
        }

        string operation = ClassifyOperation(commandText);
        activity.SetTag(DbSystemName, DbSystemValue);
        activity.SetTag(DbOperationName, operation);

        // The summary is intentionally the operation only: it carries no literals, parameters, or
        // table identifiers, keeping it low-cardinality and free of sensitive text.
        activity.SetTag(DbQuerySummary, operation);
    }

    /// <summary>
    /// Marks the span failed and records a stable error classification. No raw exception or server
    /// message text is ever attached.
    /// </summary>
    public static void TagError(Activity? activity, string errorType, string? statusCode)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error);
        if (!activity.IsAllDataRequested)
        {
            return;
        }

        activity.SetTag(ErrorType, errorType);
        if (statusCode is not null)
        {
            activity.SetTag(DbResponseStatusCode, statusCode);
        }
    }

    /// <summary>
    /// Classifies an exception into a stable <c>error.type</c> and an optional
    /// <c>db.response.status_code</c> drawn from the StarRocks error code or SQLSTATE.
    /// </summary>
    public static (string ErrorType, string? StatusCode) Classify(Exception exception)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return (ErrorCanceled, null);
            case DotRocksException dotRocks:
                string? code = dotRocks.ServerErrorCode?.ToString(CultureInfo.InvariantCulture);
                // SQLSTATE is server-controlled. Only use it as a label when it is a well-formed
                // 5-character ANSI SQLSTATE so a hostile server cannot inflate label cardinality
                // (or smuggle arbitrary text) through error.type / db.response.status_code.
                string? sqlState = IsWellFormedSqlState(dotRocks.SqlState)
                    ? dotRocks.SqlState
                    : null;
                string? statusCode = code ?? sqlState;
                return (sqlState ?? code ?? nameof(DotRocksException), statusCode);
            default:
                return (exception.GetType().Name, null);
        }
    }

    /// <summary>
    /// Maps a command's leading keyword to a bounded, low-cardinality operation name (or
    /// <c>OTHER</c>). Allocation-free: it is also used for the always-on metric label.
    /// </summary>
    public static string ClassifyOperation(string commandText)
    {
        int index = 0;
        while (index < commandText.Length && char.IsWhiteSpace(commandText[index]))
        {
            index++;
        }

        int start = index;
        while (index < commandText.Length && char.IsLetter(commandText[index]))
        {
            index++;
        }

        ReadOnlySpan<char> verb = commandText.AsSpan(start, index - start);
        foreach (string operation in KnownOperations)
        {
            if (verb.Equals(operation, StringComparison.OrdinalIgnoreCase))
            {
                return operation;
            }
        }

        return "OTHER";
    }

    private static bool IsWellFormedSqlState(string? value)
    {
        if (value is not { Length: 5 })
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Maps an execution result to a bounded metric outcome label value.</summary>
    public static string OutcomeFor(bool succeeded, string? errorType) =>
        succeeded
            ? OutcomeSuccess
            : errorType switch
            {
                ErrorTimeout => ErrorTimeout,
                ErrorCanceled => ErrorCanceled,
                _ => OutcomeError,
            };
}
