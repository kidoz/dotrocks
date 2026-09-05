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
            using JsonDocument document = JsonDocument.Parse(
                responseText,
                new JsonDocumentOptions { AllowDuplicateProperties = false }
            );
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            // The response contract accepts case-insensitive names. JsonDocument rejects exact
            // duplicates; this map also rejects aliases such as Status/status in one response.
            var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                {
                    throw new JsonException();
                }
            }

            return new DotRocksStreamLoadResult(
                GetString(properties, "Status") ?? string.Empty,
                GetString(properties, "Message"),
                GetString(properties, "Label"),
                GetNullableInt64(properties, "NumberTotalRows") ?? 0,
                GetNullableInt64(properties, "NumberLoadedRows") ?? 0,
                GetNullableInt64(properties, "NumberFilteredRows") ?? 0,
                GetNullableInt64(properties, "NumberUnselectedRows") ?? 0,
                GetNullableInt64(properties, "LoadBytes") ?? 0,
                GetNullableInt64(properties, "LoadTimeMs") ?? 0,
                CreateUri(GetString(properties, "ErrorURL")),
                GetNullableInt64(properties, "TxnId"),
                GetNullableInt32(properties, "Seq")
            );
        }
        catch (JsonException)
        {
            // JSON errors can include server-controlled property names and values. Do not
            // attach the original exception: Exception.ToString() would expose them to logs.
            throw new DotRocksStreamLoadException(
                "StarRocks returned an invalid Stream Load JSON response."
            );
        }
    }

    private static string? GetString(Dictionary<string, JsonElement> properties, string name)
    {
        if (!properties.TryGetValue(name, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => throw new JsonException(),
        };
    }

    private static long? GetNullableInt64(Dictionary<string, JsonElement> properties, string name)
    {
        if (!properties.TryGetValue(name, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out long number) => number,
            JsonValueKind.String
                when long.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long value
                ) => value,
            JsonValueKind.Null => null,
            _ => throw new JsonException(),
        };
    }

    private static int? GetNullableInt32(Dictionary<string, JsonElement> properties, string name)
    {
        long? value = GetNullableInt64(properties, name);
        if (value is null)
        {
            return null;
        }

        return value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw new JsonException();
    }

    private static Uri? CreateUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ? uri : null;
}
