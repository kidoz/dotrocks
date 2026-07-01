using System.Globalization;
using System.Text;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Results;

internal static class ColumnTypeMapper
{
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFF",
    ];

    public static string GetDataTypeName(byte type) =>
        ToColumnType(type) switch
        {
            ColumnType.Decimal => "DECIMAL",
            ColumnType.Tiny => "TINY",
            ColumnType.Short => "SHORT",
            ColumnType.Long => "LONG",
            ColumnType.Float => "FLOAT",
            ColumnType.Double => "DOUBLE",
            ColumnType.Null => "NULL",
            ColumnType.Timestamp => "TIMESTAMP",
            ColumnType.LongLong => "LONGLONG",
            ColumnType.Int24 => "INT24",
            ColumnType.Date => "DATE",
            ColumnType.Time => "TIME",
            ColumnType.DateTime => "DATETIME",
            ColumnType.Year => "YEAR",
            ColumnType.NewDate => "NEWDATE",
            ColumnType.VarChar => "VARCHAR",
            ColumnType.Bit => "BIT",
            ColumnType.Json => "JSON",
            ColumnType.NewDecimal => "NEWDECIMAL",
            ColumnType.Enum => "ENUM",
            ColumnType.Set => "SET",
            ColumnType.TinyBlob => "TINYBLOB",
            ColumnType.MediumBlob => "MEDIUMBLOB",
            ColumnType.LongBlob => "LONGBLOB",
            ColumnType.Blob => "BLOB",
            ColumnType.VarString => "VAR_STRING",
            ColumnType.String => "STRING",
            ColumnType.Geometry => "GEOMETRY",
            _ => throw CreateUnsupportedTypeException(type),
        };

    // StarRocks sends BOOLEAN as TINYINT with a display length of 1 (the MySQL `tinyint(1)`
    // convention); a wider TINYINT uses length 4. This is how BOOLEAN is distinguished on the wire.
    private const uint BooleanColumnLength = 1;

    public static Type GetFieldType(byte type, uint columnLength) =>
        ToColumnType(type) switch
        {
            ColumnType.Tiny => columnLength == BooleanColumnLength ? typeof(bool) : typeof(sbyte),
            ColumnType.Short => typeof(short),
            ColumnType.Long or ColumnType.Int24 or ColumnType.Year => typeof(int),
            ColumnType.LongLong => typeof(long),
            ColumnType.Decimal or ColumnType.NewDecimal => typeof(DotRocksDecimal),
            ColumnType.Float => typeof(float),
            ColumnType.Double => typeof(double),
            ColumnType.Date or ColumnType.NewDate or ColumnType.DateTime or ColumnType.Timestamp =>
                typeof(DateTime),
            ColumnType.Time => typeof(TimeSpan),
            ColumnType.Null => typeof(DBNull),
            ColumnType.TinyBlob
            or ColumnType.MediumBlob
            or ColumnType.LongBlob
            or ColumnType.Blob
            or ColumnType.Bit => typeof(byte[]),
            ColumnType.VarChar
            or ColumnType.Json
            or ColumnType.Enum
            or ColumnType.Set
            or ColumnType.VarString
            or ColumnType.String
            or ColumnType.Geometry => typeof(string),
            _ => throw CreateUnsupportedTypeException(type),
        };

    public static object ParseTextValue(byte type, uint columnLength, ReadOnlySpan<byte> bytes)
    {
        ColumnType columnType = ToColumnType(type);

        // Binary types (BLOB family and BIT) are raw bytes; decoding them as UTF-8 would corrupt
        // non-text byte sequences. Return them without touching the text decoder.
        switch (columnType)
        {
            case ColumnType.TinyBlob:
            case ColumnType.MediumBlob:
            case ColumnType.LongBlob:
            case ColumnType.Blob:
            case ColumnType.Bit:
                return bytes.ToArray();
        }

        try
        {
            // Numeric types parse straight from the UTF-8 bytes (IUtf8SpanParsable), so a wide
            // numeric result set decodes with no per-value string allocation. Only the branches
            // that need culture/format-exact parsing or return text decode the bytes to a string.
            return columnType switch
            {
                ColumnType.Tiny when columnLength == BooleanColumnLength => ParseBoolean(bytes),
                ColumnType.Tiny => sbyte.Parse(
                    bytes,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.Short => short.Parse(
                    bytes,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.Long or ColumnType.Int24 or ColumnType.Year => int.Parse(
                    bytes,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.LongLong => long.Parse(
                    bytes,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.Decimal or ColumnType.NewDecimal => DotRocksDecimal.Parse(
                    Encoding.UTF8.GetString(bytes)
                ),
                ColumnType.Float => float.Parse(
                    bytes,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.Double => double.Parse(
                    bytes,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.Date or ColumnType.NewDate => DateTime.ParseExact(
                    Encoding.UTF8.GetString(bytes),
                    DateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                ),
                ColumnType.DateTime or ColumnType.Timestamp => DateTime.ParseExact(
                    Encoding.UTF8.GetString(bytes),
                    DateTimeFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                ),
                ColumnType.Time => ParseTime(bytes),
                ColumnType.Null
                or ColumnType.VarChar
                or ColumnType.Json
                or ColumnType.Enum
                or ColumnType.Set
                or ColumnType.VarString
                or ColumnType.String
                or ColumnType.Geometry => Encoding.UTF8.GetString(bytes),
                _ => throw CreateUnsupportedTypeException(type),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // The server returned a value that does not match the column's declared type.
            throw new MalformedPacketException(
                $"StarRocks returned a value that is not valid for column type {columnType} (0x{type:X2}).",
                ex
            );
        }
    }

    // The MySQL TIME range is -838:59:59 to 838:59:59; a value outside it is not a legal wire value.
    private const int MaxTimeHours = 838;

    // TIME text follows the MySQL duration convention `[-]H..H:MM:SS[.ffffff]`: the hour component
    // can exceed 23 (e.g. a timediff() spanning days, up to 838:59:59), which TimeSpan.Parse
    // rejects. Parse the components explicitly and culture-invariantly from the ASCII bytes.
    private static TimeSpan ParseTime(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> text = bytes;
        bool negative = text.Length > 0 && text[0] == (byte)'-';
        if (negative)
        {
            text = text[1..];
        }

        int firstColon = text.IndexOf((byte)':');
        if (firstColon < 1)
        {
            throw new FormatException("TIME value does not contain an hour component.");
        }

        int hours = ParseTimeComponent(text[..firstColon]);
        // Rejecting out-of-range hours also keeps the tick arithmetic below far from overflow.
        if (hours > MaxTimeHours)
        {
            throw new FormatException($"TIME hours must be between 0 and {MaxTimeHours}.");
        }

        text = text[(firstColon + 1)..];
        if (text.Length < 5 || text[2] != (byte)':')
        {
            throw new FormatException("TIME value does not contain minute and second components.");
        }

        int minutes = ParseTimeComponent(text[..2]);
        int seconds = ParseTimeComponent(text.Slice(3, 2));
        if (minutes > 59 || seconds > 59)
        {
            throw new FormatException("TIME minutes and seconds must be between 0 and 59.");
        }

        long microseconds = 0;
        if (text.Length > 5)
        {
            ReadOnlySpan<byte> fraction = text[5] == (byte)'.' ? text[6..] : default;
            if (fraction.IsEmpty || fraction.Length > 6)
            {
                throw new FormatException("TIME value has a malformed fractional-second part.");
            }

            microseconds = ParseTimeComponent(fraction);
            for (int i = fraction.Length; i < 6; i++)
            {
                microseconds *= 10;
            }
        }

        long ticks =
            (hours * TimeSpan.TicksPerHour)
            + (minutes * TimeSpan.TicksPerMinute)
            + (seconds * TimeSpan.TicksPerSecond)
            + (microseconds * (TimeSpan.TicksPerMillisecond / 1000));
        return new TimeSpan(negative ? -ticks : ticks);
    }

    private static int ParseTimeComponent(ReadOnlySpan<byte> digits) =>
        int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    private static bool ParseBoolean(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 1)
        {
            switch (bytes[0])
            {
                case (byte)'1':
                    return true;
                case (byte)'0':
                    return false;
            }
        }

        return bool.Parse(Encoding.UTF8.GetString(bytes));
    }

    private static ColumnType ToColumnType(byte type)
    {
        var columnType = (ColumnType)type;
        if (Enum.IsDefined(columnType))
        {
            return columnType;
        }

        throw CreateUnsupportedTypeException(type);
    }

    private static NotSupportedException CreateUnsupportedTypeException(byte type) =>
        new($"Unsupported StarRocks column type 0x{type:X2}.");
}
