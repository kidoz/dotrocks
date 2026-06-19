using System.Globalization;
using System.Text;

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

    public static Type GetFieldType(byte type) =>
        ToColumnType(type) switch
        {
            ColumnType.Tiny
            or ColumnType.Short
            or ColumnType.Long
            or ColumnType.Int24
            or ColumnType.Year => typeof(int),
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
            or ColumnType.Blob => typeof(byte[]),
            ColumnType.VarChar
            or ColumnType.Json
            or ColumnType.Enum
            or ColumnType.Set
            or ColumnType.VarString
            or ColumnType.String
            or ColumnType.Geometry
            or ColumnType.Bit => typeof(string),
            _ => throw CreateUnsupportedTypeException(type),
        };

    public static object ParseTextValue(byte type, ReadOnlySpan<byte> bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        return ToColumnType(type) switch
        {
            ColumnType.Tiny
            or ColumnType.Short
            or ColumnType.Long
            or ColumnType.Int24
            or ColumnType.Year => int.Parse(
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
            ColumnType.Float => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
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
            ColumnType.TinyBlob
            or ColumnType.MediumBlob
            or ColumnType.LongBlob
            or ColumnType.Blob => bytes.ToArray(),
            ColumnType.Null
            or ColumnType.VarChar
            or ColumnType.Json
            or ColumnType.Enum
            or ColumnType.Set
            or ColumnType.VarString
            or ColumnType.String
            or ColumnType.Geometry
            or ColumnType.Bit => text,
            _ => throw CreateUnsupportedTypeException(type),
        };
    }

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
