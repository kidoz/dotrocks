using System.Globalization;

namespace DotRocks.Data.Loading;

/// <summary>
/// Configures a StarRocks Stream Load transaction.
/// </summary>
public sealed class DotRocksStreamLoadTransactionOptions
{
    /// <summary>
    /// Gets or sets the required StarRocks transaction label.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the transaction can load multiple tables. Requires
    /// StarRocks 4.0 or later; on earlier lines (single-table only) beginning the transaction is
    /// rejected.
    /// </summary>
    public bool IsMultiTable { get; set; }

    /// <summary>
    /// Gets or sets the timeout from PREPARE to PREPARED.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Gets or sets the idle timeout before StarRocks rolls back an idle transaction.
    /// </summary>
    public TimeSpan? IdleTimeout { get; set; }

    /// <summary>
    /// Gets or sets the timeout from PREPARED to COMMITTED.
    /// </summary>
    public TimeSpan? PreparedTimeout { get; set; }

    internal string GetRequiredLabel()
    {
        if (string.IsNullOrWhiteSpace(Label))
        {
            throw new ArgumentException(
                "A Stream Load transaction label is required.",
                nameof(Label)
            );
        }

        ValidateHeaderValue(Label);
        return Label;
    }

    internal IReadOnlyDictionary<string, string> BuildBeginHeaders(
        string databaseName,
        string tableName
    )
    {
        var headers = BuildCommonHeaders(databaseName);
        AddHeader(headers, "table", tableName);
        AddTimeSpanHeader(headers, "timeout", Timeout);
        AddTimeSpanHeader(headers, "idle_transaction_timeout", IdleTimeout);
        return headers;
    }

    internal IReadOnlyDictionary<string, string> BuildLoadHeaders(
        string databaseName,
        string tableName,
        DotRocksStreamLoadOptions loadOptions,
        DotRocksStreamLoadFormat format
    )
    {
        var headers = new Dictionary<string, string>(
            loadOptions.BuildHeaders(format),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (KeyValuePair<string, string> header in BuildCommonHeaders(databaseName))
        {
            headers[header.Key] = header.Value;
        }

        AddHeader(headers, "table", tableName);
        return headers;
    }

    internal IReadOnlyDictionary<string, string> BuildPrepareHeaders(string databaseName)
    {
        var headers = BuildCommonHeaders(databaseName);
        AddTimeSpanHeader(headers, "prepared_timeout", PreparedTimeout);
        return headers;
    }

    internal IReadOnlyDictionary<string, string> BuildCompletionHeaders(string databaseName) =>
        BuildCommonHeaders(databaseName);

    private Dictionary<string, string> BuildCommonHeaders(string databaseName)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["label"] = GetRequiredLabel(),
            ["db"] = databaseName,
        };

        if (IsMultiTable)
        {
            headers["transaction_type"] = "multi";
        }

        return headers;
    }

    private static void AddHeader(Dictionary<string, string> headers, string name, string value)
    {
        ValidateHeaderValue(value);
        headers[name] = value;
    }

    private static void AddTimeSpanHeader(
        Dictionary<string, string> headers,
        string name,
        TimeSpan? value
    )
    {
        if (value is null)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.Value, TimeSpan.Zero);
        headers[name] = ((int)value.Value.TotalSeconds).ToString(CultureInfo.InvariantCulture);
    }

    private static void ValidateHeaderValue(string value)
    {
        if (
            value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                "Stream Load transaction header values must not contain CR or LF."
            );
        }
    }
}
