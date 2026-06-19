using System.Globalization;
using System.Text;

namespace DotRocks.Data.Protocol.Commands;

internal static class SqlLiteralFormatter
{
    public static string Format(object? value) =>
        value switch
        {
            null => "NULL",
            DBNull => "NULL",
            bool boolValue => boolValue ? "TRUE" : "FALSE",
            sbyte sbyteValue => sbyteValue.ToString(CultureInfo.InvariantCulture),
            byte byteValue => byteValue.ToString(CultureInfo.InvariantCulture),
            short shortValue => shortValue.ToString(CultureInfo.InvariantCulture),
            ushort ushortValue => ushortValue.ToString(CultureInfo.InvariantCulture),
            int intValue => intValue.ToString(CultureInfo.InvariantCulture),
            uint uintValue => uintValue.ToString(CultureInfo.InvariantCulture),
            long longValue => longValue.ToString(CultureInfo.InvariantCulture),
            ulong ulongValue => ulongValue.ToString(CultureInfo.InvariantCulture),
            float floatValue => FormatSingle(floatValue),
            double doubleValue => FormatDouble(doubleValue),
            decimal decimalValue => decimalValue.ToString(CultureInfo.InvariantCulture),
            DotRocksDecimal dotRocksDecimalValue => dotRocksDecimalValue.ToString(),
            string stringValue => FormatString(stringValue),
            DateTime dateTimeValue => FormatString(
                dateTimeValue.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture)
            ),
            DateOnly dateOnlyValue => FormatString(
                dateOnlyValue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            ),
            TimeOnly timeOnlyValue => FormatString(
                timeOnlyValue.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture)
            ),
            Guid guidValue => FormatString(guidValue.ToString("D", CultureInfo.InvariantCulture)),
            byte[] bytes => FormatBytes(bytes),
            _ => throw new NotSupportedException(
                $"Parameter values of type '{value.GetType().FullName}' are not supported."
            ),
        };

    private static string FormatSingle(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            throw new NotSupportedException("NaN and infinity parameter values are not supported.");
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new NotSupportedException("NaN and infinity parameter values are not supported.");
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
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
        return builder.ToString();
    }

    private static string FormatBytes(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2 + 3);
        builder.Append("X'");
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        builder.Append('\'');
        return builder.ToString();
    }
}
