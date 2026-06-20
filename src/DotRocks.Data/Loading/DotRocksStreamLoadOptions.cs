using System.Globalization;

namespace DotRocks.Data.Loading;

/// <summary>
/// Configures a StarRocks Stream Load request.
/// </summary>
public sealed class DotRocksStreamLoadOptions
{
    /// <summary>
    /// Gets or sets the optional StarRocks load label.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets the optional column mapping expression.
    /// </summary>
    public string? Columns { get; set; }

    /// <summary>
    /// Gets or sets the optional row filter expression.
    /// </summary>
    public string? Where { get; set; }

    /// <summary>
    /// Gets or sets the CSV column separator. Use escaped values such as <c>\t</c> or <c>\x01</c>.
    /// </summary>
    public string? ColumnSeparator { get; set; }

    /// <summary>
    /// Gets or sets the CSV row delimiter. Use escaped values such as <c>\n</c>.
    /// </summary>
    public string? RowDelimiter { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether StarRocks strict mode is enabled.
    /// </summary>
    public bool? StrictMode { get; set; }

    /// <summary>
    /// Gets or sets the maximum allowed filtered-row ratio, from 0 through 1.
    /// </summary>
    public double? MaxFilterRatio { get; set; }

    /// <summary>
    /// Gets or sets the StarRocks load timeout.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a JSON payload contains an outer array.
    /// </summary>
    public bool? StripOuterArray { get; set; }

    /// <summary>
    /// Gets or sets the optional StarRocks JSON paths expression.
    /// </summary>
    public string? JsonPaths { get; set; }

    internal IReadOnlyDictionary<string, string> BuildHeaders(DotRocksStreamLoadFormat format)
    {
        Validate();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["format"] = format == DotRocksStreamLoadFormat.Csv ? "csv" : "json",
        };

        // A label makes a load idempotent: StarRocks rejects a duplicate label, so a resend
        // (e.g. after a redirect) cannot double-load. Generate one when the caller omits it.
        AddHeader(headers, "label", string.IsNullOrEmpty(Label) ? GenerateLabel() : Label);
        AddHeader(headers, "columns", Columns);
        AddHeader(headers, "where", Where);
        AddHeader(headers, "column_separator", ColumnSeparator);
        AddHeader(headers, "row_delimiter", RowDelimiter);
        if (StrictMode is not null)
        {
            headers["strict_mode"] = FormatBoolean(StrictMode.Value);
        }

        if (MaxFilterRatio is not null)
        {
            headers["max_filter_ratio"] = MaxFilterRatio.Value.ToString(
                CultureInfo.InvariantCulture
            );
        }

        if (Timeout is not null)
        {
            headers["timeout"] = ((int)Timeout.Value.TotalSeconds).ToString(
                CultureInfo.InvariantCulture
            );
        }

        if (format == DotRocksStreamLoadFormat.Json)
        {
            if (StripOuterArray is not null)
            {
                headers["strip_outer_array"] = FormatBoolean(StripOuterArray.Value);
            }

            AddHeader(headers, "jsonpaths", JsonPaths);
        }

        return headers;
    }

    private void Validate()
    {
        ValidateHeaderValue(Label);
        ValidateHeaderValue(Columns);
        ValidateHeaderValue(Where);
        ValidateHeaderValue(ColumnSeparator);
        ValidateHeaderValue(RowDelimiter);
        ValidateHeaderValue(JsonPaths);

        if (
            MaxFilterRatio is not null
            && (MaxFilterRatio is < 0 or > 1 || double.IsNaN(MaxFilterRatio.Value))
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxFilterRatio),
                MaxFilterRatio,
                "MaxFilterRatio must be between 0 and 1."
            );
        }

        if (Timeout is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Timeout.Value, TimeSpan.Zero);
        }
    }

    private static string GenerateLabel() => "dotrocks_" + Guid.NewGuid().ToString("N");

    private static void AddHeader(Dictionary<string, string> headers, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            headers[name] = value;
        }
    }

    private static string FormatBoolean(bool value) => value ? "true" : "false";

    private static void ValidateHeaderValue(string? value)
    {
        if (value is null)
        {
            return;
        }

        if (
            value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
        )
        {
            throw new ArgumentException("Stream Load header values must not contain CR or LF.");
        }
    }
}

internal enum DotRocksStreamLoadFormat
{
    Csv,
    Json,
}
