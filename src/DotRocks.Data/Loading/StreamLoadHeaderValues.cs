using System.Globalization;

namespace DotRocks.Data.Loading;

internal static class StreamLoadHeaderValues
{
    public static void Validate(string? value, string parameterName)
    {
        if (value is not null && value.Any(character => character is < ' ' or > '~'))
        {
            throw new ArgumentException(
                "Stream Load header values must contain printable ASCII characters only. Use escaped separators and delimiters.",
                parameterName
            );
        }
    }

    public static string FormatTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value.TotalSeconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Stream Load timeouts must be positive and fit in a signed 32-bit count of seconds."
            );
        }
        return ((int)Math.Ceiling(value.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }
}
