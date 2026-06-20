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

        string text = Encoding.UTF8.GetString(bytes);
        try
        {
            return columnType switch
            {
                ColumnType.Tiny when columnLength == BooleanColumnLength => ParseBoolean(text),
                ColumnType.Tiny => sbyte.Parse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.Short => short.Parse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.Long or ColumnType.Int24 or ColumnType.Year => int.Parse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.LongLong => long.Parse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.Decimal or ColumnType.NewDecimal => DotRocksDecimal.Parse(text),
                ColumnType.Float => float.Parse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.Double => double.Parse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture
                ),
                ColumnType.Date or ColumnType.NewDate => DateTime.ParseExact(
                    text,
                    DateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                ),
                ColumnType.DateTime or ColumnType.Timestamp => DateTime.ParseExact(
                    text,
                    DateTimeFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                ),
                ColumnType.Time => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
                ColumnType.Null
                or ColumnType.VarChar
                or ColumnType.Json
                or ColumnType.Enum
                or ColumnType.Set
                or ColumnType.VarString
                or ColumnType.String
                or ColumnType.Geometry => text,
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

    private static bool ParseBoolean(string text) =>
        text switch
        {
            "1" => true,
            "0" => false,
            _ => bool.Parse(text),
        };

    private static ColumnType ToColumnType(byte type)
    {
        if (Enum.IsDefined(typeof(ColumnType), type))
        {
            return (ColumnType)type;
        }

        throw CreateUnsupportedTypeException(type);
    }

    private static NotSupportedException CreateUnsupportedTypeException(byte type) =>
        new($"Unsupported StarRocks column type 0x{type:X2}.");
}
