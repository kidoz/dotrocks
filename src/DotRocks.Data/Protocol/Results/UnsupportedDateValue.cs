namespace DotRocks.Data.Protocol.Results;

// The packet is fully decoded before an accessor reports the CLR range limit. This preserves
// other columns, subsequent rows, and the ability to drain and reuse the connection.
internal sealed class UnsupportedDateValue
{
    public static UnsupportedDateValue Instance { get; } = new();

    private UnsupportedDateValue() { }

    public static object GetValue(object? value) =>
        value is UnsupportedDateValue
            ? throw new DotRocksUnsupportedFeatureException(
                "StarRocks DATE/DATETIME year zero cannot be represented by System.DateTime (years 1 through 9999). Read this column with CAST(column AS STRING)."
            )
            : value ?? DBNull.Value;
}
