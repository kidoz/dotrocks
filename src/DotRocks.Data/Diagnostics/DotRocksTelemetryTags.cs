using System.Diagnostics;
using System.Globalization;
using DotRocks.Data.Loading;

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

    // Stable, low-cardinality error classifications.
    public const string ErrorTimeout = "timeout";
    public const string ErrorCanceled = "canceled";

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
                string? statusCode = code ?? dotRocks.SqlState;
                return (dotRocks.SqlState ?? code ?? nameof(DotRocksException), statusCode);
            default:
                return (exception.GetType().Name, null);
        }
    }

    private static string ClassifyOperation(string commandText)
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

        if (index == start)
        {
            return "OTHER";
        }

        // Restrict to a known set so the attribute stays low-cardinality for arbitrary input.
        return commandText[start..index].ToUpperInvariant() switch
        {
            "SELECT" => "SELECT",
            "INSERT" => "INSERT",
            "UPDATE" => "UPDATE",
            "DELETE" => "DELETE",
            "SET" => "SET",
            "USE" => "USE",
            "CREATE" => "CREATE",
            "DROP" => "DROP",
            "ALTER" => "ALTER",
            "SHOW" => "SHOW",
            "WITH" => "WITH",
            "EXPLAIN" => "EXPLAIN",
            "TRUNCATE" => "TRUNCATE",
            "GRANT" => "GRANT",
            "REVOKE" => "REVOKE",
            "BEGIN" => "BEGIN",
            "COMMIT" => "COMMIT",
            "ROLLBACK" => "ROLLBACK",
            _ => "OTHER",
        };
    }
}
