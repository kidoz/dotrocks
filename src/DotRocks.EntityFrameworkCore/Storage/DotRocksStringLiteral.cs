using System.Buffers;
using System.Text;

namespace DotRocks.EntityFrameworkCore.Storage;

/// <summary>
/// Builds single-quoted StarRocks string literals with the same escaping rules the native driver's
/// literal formatter applies. StarRocks treats backslash as an escape character inside string
/// literals, so a value with a trailing backslash or a control character must be escaped or it
/// corrupts the literal (and is an injection risk). Centralized here so every EF Core literal
/// generator escapes identically.
/// </summary>
internal static class DotRocksStringLiteral
{
    // NUL, single quote, backslash, LF, CR, and Ctrl-Z are the characters that must be escaped
    // inside a single-quoted StarRocks string literal. Most values contain none of them, so one
    // vectorized scan lets the common case skip the per-character escaping loop entirely.
    private static readonly SearchValues<char> CharactersRequiringEscape = SearchValues.Create(
        "\0'\\\n\r\u001A"
    );

    /// <summary>Returns <paramref name="value"/> as an escaped, single-quoted StarRocks literal.</summary>
    public static string Generate(string value)
    {
        // Fast path: no escapable character, so the literal is just the value wrapped in quotes.
        if (!value.AsSpan().ContainsAny(CharactersRequiringEscape))
        {
            return string.Concat("'", value, "'");
        }

        var builder = new StringBuilder(value.Length + 2);
        Append(builder, value);
        return builder.ToString();
    }

    /// <summary>Appends <paramref name="value"/> as an escaped, single-quoted StarRocks literal.</summary>
    public static void Append(StringBuilder builder, string value)
    {
        builder.Append('\'');
        foreach (char character in value)
        {
            switch (character)
            {
                case '\0':
                    builder.Append("\\0");
                    break;
                case '\'':
                    builder.Append("''");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\u001A':
                    builder.Append("\\Z");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('\'');
    }
}
