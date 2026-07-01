using System.Buffers.Binary;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Results;

/// <summary>
/// Decodes a single binary-protocol result row (the format used by <c>COM_STMT_EXECUTE</c>) into CLR
/// values. Fixed-width numerics and temporal values are decoded from their binary layout; all other
/// types (decimal, string, JSON, blob, …) are length-encoded text in the binary protocol too, so
/// they reuse <see cref="ColumnTypeMapper.ParseTextValue"/>.
/// </summary>
internal static class BinaryResultRowDecoder
{
    public static object?[] Decode(
        ReadOnlySpan<byte> payload,
        IReadOnlyList<ColumnDefinition> columns
    )
    {
        var reader = new ProtocolReader(payload);
        byte header = reader.ReadByte();
        if (header != 0x00)
        {
            throw new MalformedPacketException(
                $"Unexpected binary result row header 0x{header:X2}."
            );
        }

        // NULL bitmap: (column_count + 7 + 2) / 8 bytes, offset by 2 reserved bits.
        int bitmapLength = (columns.Count + 7 + 2) / 8;
        ReadOnlySpan<byte> nullBitmap = reader.ReadBytes(bitmapLength);

        var values = new object?[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            int bitPosition = i + 2;
            bool isNull = (nullBitmap[bitPosition / 8] & (1 << (bitPosition % 8))) != 0;
            values[i] = isNull ? null : DecodeValue(ref reader, columns[i]);
        }

        return values;
    }

    private static object DecodeValue(ref ProtocolReader reader, ColumnDefinition column)
    {
        switch ((ColumnType)column.ColumnType)
        {
            case ColumnType.Tiny:
                byte tiny = reader.ReadByte();
                return column.ColumnLength == 1 ? tiny != 0 : (sbyte)tiny;
            case ColumnType.Short:
                return (short)reader.ReadFixedInteger(2);
            case ColumnType.Year:
                // YEAR is a 2-byte unsigned wire value; box it as int to match GetFieldType and
                // the text protocol, which both expose YEAR as System.Int32.
                return (int)(ushort)reader.ReadFixedInteger(2);
            case ColumnType.Long:
            case ColumnType.Int24:
                return (int)(uint)reader.ReadFixedInteger(4);
            case ColumnType.LongLong:
                return (long)reader.ReadFixedInteger(8);
            case ColumnType.Float:
                return BinaryPrimitives.ReadSingleLittleEndian(reader.ReadBytes(4));
            case ColumnType.Double:
                return BinaryPrimitives.ReadDoubleLittleEndian(reader.ReadBytes(8));
            case ColumnType.Date:
            case ColumnType.NewDate:
            case ColumnType.DateTime:
            case ColumnType.Timestamp:
                return DecodeDateTime(ref reader);
            case ColumnType.Time:
                return DecodeTime(ref reader);
            default:
                // Decimal, varchar, string, JSON, blob, bit, etc. are length-encoded text.
                ReadOnlySpan<byte> bytes = reader.ReadLengthEncodedBytes(out _);
                return ColumnTypeMapper.ParseTextValue(
                    column.ColumnType,
                    column.ColumnLength,
                    bytes
                );
        }
    }

    private static DateTime DecodeDateTime(ref ProtocolReader reader)
    {
        byte length = reader.ReadByte();
        if (length == 0)
        {
            return default;
        }

        int year = (int)reader.ReadFixedInteger(2);
        int month = reader.ReadByte();
        int day = reader.ReadByte();
        int hour = 0;
        int minute = 0;
        int second = 0;
        long microseconds = 0;
        if (length >= 7)
        {
            hour = reader.ReadByte();
            minute = reader.ReadByte();
            second = reader.ReadByte();
        }

        if (length >= 11)
        {
            microseconds = (uint)reader.ReadFixedInteger(4);
        }

        // A hostile or buggy server may send out-of-range components; surface that as a controlled
        // protocol error rather than letting the DateTime constructor throw an uncontrolled one.
        try
        {
            return new DateTime(
                year,
                month,
                day,
                hour,
                minute,
                second,
                DateTimeKind.Unspecified
            ).AddTicks(microseconds * (TimeSpan.TicksPerMillisecond / 1000));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new MalformedPacketException(
                "Binary DATETIME value has out-of-range date or time components.",
                ex
            );
        }
    }

    private static TimeSpan DecodeTime(ref ProtocolReader reader)
    {
        byte length = reader.ReadByte();
        if (length == 0)
        {
            return TimeSpan.Zero;
        }

        bool negative = reader.ReadByte() != 0;
        uint days = (uint)reader.ReadFixedInteger(4);
        int hours = reader.ReadByte();
        int minutes = reader.ReadByte();
        int seconds = reader.ReadByte();
        long microseconds = length >= 12 ? (uint)reader.ReadFixedInteger(4) : 0;

        // Reject a duration that cannot fit in a TimeSpan rather than overflowing silently.
        if (days > TimeSpan.MaxValue.Days)
        {
            throw new MalformedPacketException(
                $"Binary TIME value of {days} day(s) exceeds the supported duration."
            );
        }

        try
        {
            TimeSpan value =
                new TimeSpan((int)days, hours, minutes, seconds)
                + TimeSpan.FromTicks(microseconds * (TimeSpan.TicksPerMillisecond / 1000));
            return negative ? -value : value;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new MalformedPacketException("Binary TIME value is out of range.", ex);
        }
    }
}
