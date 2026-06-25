using System.Text.Json;

namespace DotRocks.Data;

/// <summary>
/// An immutable, lossless wrapper around a StarRocks <c>JSON</c> value. DotRocks returns JSON over
/// the text protocol as the exact bytes the server produced; this type preserves that raw text so no
/// information (key order, spacing, numeric formatting) is lost, and offers opt-in typed access via
/// <see cref="Parse"/>. Read a JSON column with <c>reader.GetFieldValue&lt;DotRocksJson&gt;(ordinal)</c>.
/// </summary>
public sealed class DotRocksJson : IEquatable<DotRocksJson>
{
    /// <summary>Initializes a new instance of the <see cref="DotRocksJson"/> class.</summary>
    /// <param name="rawText">The raw JSON text exactly as returned by StarRocks.</param>
    public DotRocksJson(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        RawText = rawText;
    }

    /// <summary>Gets the raw JSON text exactly as StarRocks returned it.</summary>
    public string RawText { get; }

    /// <summary>
    /// Parses the raw text into a new <see cref="JsonDocument"/> that the caller owns and must
    /// dispose. The document is independent of the reader, so it remains valid after the reader
    /// advances or closes.
    /// </summary>
    /// <exception cref="JsonException">The raw text is not valid JSON.</exception>
    public JsonDocument Parse() => JsonDocument.Parse(RawText);

    /// <inheritdoc />
    public override string ToString() => RawText;

    /// <summary>
    /// Compares the raw JSON text ordinally. Two values that are semantically equal but formatted
    /// differently (for example different spacing or key order) are not considered equal, because
    /// this type preserves the server's exact representation.
    /// </summary>
    public bool Equals(DotRocksJson? other) =>
        other is not null && string.Equals(RawText, other.RawText, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DotRocksJson);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(RawText);
}
