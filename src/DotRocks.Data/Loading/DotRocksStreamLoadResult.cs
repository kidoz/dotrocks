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
        Uri? errorUrl
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
    /// Gets a value indicating whether StarRocks reported the load as successful.
    /// </summary>
    public bool IsSuccess => string.Equals(Status, "Success", StringComparison.OrdinalIgnoreCase);

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
                CreateUri(GetString(root, "ErrorURL"))
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
