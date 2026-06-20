using System.Globalization;
using System.Text.Json;

namespace DotRocks.Data.Loading;

/// <summary>
/// Represents the response returned by a StarRocks Stream Load request.
/// </summary>
public sealed class DotRocksStreamLoadResult
{
    internal DotRocksStreamLoadResult(
        string status,
        string? message,
        string? label,
        long numberTotalRows,
        long numberLoadedRows,
        long numberFilteredRows,
        long numberUnselectedRows,
        long loadBytes,
        long loadTimeMilliseconds,
        Uri? errorUrl,
        long? transactionId,
        int? sequence
    )
    {
        Status = status;
        Message = message;
        Label = label;
        NumberTotalRows = numberTotalRows;
        NumberLoadedRows = numberLoadedRows;
        NumberFilteredRows = numberFilteredRows;
        NumberUnselectedRows = numberUnselectedRows;
        LoadBytes = loadBytes;
        LoadTimeMilliseconds = loadTimeMilliseconds;
        ErrorUrl = errorUrl;
        TransactionId = transactionId;
        Sequence = sequence;
    }

    /// <summary>
    /// Gets the StarRocks Stream Load status.
    /// </summary>
    public string Status { get; }

    /// <summary>
    /// Gets the optional StarRocks response message.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Gets the load label reported by StarRocks.
    /// </summary>
    public string? Label { get; }

    /// <summary>
    /// Gets the total number of rows observed by StarRocks.
    /// </summary>
    public long NumberTotalRows { get; }

    /// <summary>
    /// Gets the number of rows loaded by StarRocks.
    /// </summary>
    public long NumberLoadedRows { get; }

    /// <summary>
    /// Gets the number of rows filtered by StarRocks.
    /// </summary>
    public long NumberFilteredRows { get; }

    /// <summary>
    /// Gets the number of rows excluded by StarRocks.
    /// </summary>
    public long NumberUnselectedRows { get; }

    /// <summary>
    /// Gets the number of payload bytes processed by StarRocks.
    /// </summary>
    public long LoadBytes { get; }

    /// <summary>
    /// Gets the load duration reported by StarRocks, in milliseconds.
    /// </summary>
    public long LoadTimeMilliseconds { get; }

    /// <summary>
    /// Gets the optional URL for load-error details.
    /// </summary>
    public Uri? ErrorUrl { get; }

    /// <summary>
    /// Gets the StarRocks transaction identifier, when the response includes one.
    /// </summary>
    public long? TransactionId { get; }

    /// <summary>
    /// Gets the transaction load sequence, when the response includes one.
    /// </summary>
    public int? Sequence { get; }

    /// <summary>
    /// Gets a value indicating whether the load was applied but its visibility publish timed out.
    /// The rows are written; they may become queryable slightly later.
    /// </summary>
    public bool IsPublishTimeout =>
        string.Equals(Status, "Publish Timeout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether StarRocks reported the load as successful. A publish
    /// timeout counts as success because the data was written.
    /// </summary>
    public bool IsSuccess =>
        string.Equals(Status, "Success", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase)
        || IsPublishTimeout;

    internal static DotRocksStreamLoadResult Parse(string responseText)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseText);
            JsonElement root = document.RootElement;
            return new DotRocksStreamLoadResult(
                GetString(root, "Status") ?? string.Empty,
                GetString(root, "Message"),
                GetString(root, "Label"),
                GetInt64(root, "NumberTotalRows"),
                GetInt64(root, "NumberLoadedRows"),
                GetInt64(root, "NumberFilteredRows"),
                GetInt64(root, "NumberUnselectedRows"),
                GetInt64(root, "LoadBytes"),
                GetInt64(root, "LoadTimeMs"),
                CreateUri(GetString(root, "ErrorURL")),
                GetNullableInt64(root, "TxnId"),
                GetNullableInt32(root, "Seq")
            );
        }
        catch (JsonException ex)
        {
            throw new DotRocksStreamLoadException(
                "StarRocks returned an invalid Stream Load JSON response.",
                ex
            );
        }
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static long GetInt64(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out JsonElement property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetInt64(),
            JsonValueKind.String
                when long.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long value
                ) => value,
            _ => 0,
        };
    }

    private static long? GetNullableInt64(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetInt64(),
            JsonValueKind.String
                when long.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long value
                ) => value,
            _ => null,
        };
    }

    private static int? GetNullableInt32(JsonElement root, string name)
    {
        long? value = GetNullableInt64(root, name);
        if (value is null)
        {
            return null;
        }

        return value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
    }

    private static Uri? CreateUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ? uri : null;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement property)
    {
        foreach (JsonProperty jsonProperty in root.EnumerateObject())
        {
            if (string.Equals(jsonProperty.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = jsonProperty.Value;
                return true;
            }
        }

        property = default;
        return false;
    }
}
