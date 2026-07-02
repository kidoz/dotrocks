using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Commands;

/// <summary>
/// Encodes the <c>COM_STMT_EXECUTE</c> request body, including the NULL bitmap, the parameter
/// type block, and the binary parameter values. Fixed-width numerics use their native binary
/// layout; decimals, dates, and other non-numeric values are sent as <c>VAR_STRING</c> text, which
/// StarRocks casts to the placeholder's type.
/// </summary>
internal static class BinaryParameterEncoder
{
    private const byte ComStmtExecute = 0x17;
    private const byte CursorTypeNoCursor = 0x00;
    private const byte NewParamsBound = 0x01;
    private const byte UnsignedFlag = 0x80;

    // MySQL binary protocol type codes used for parameters.
    private const byte TypeTiny = 0x01;
    private const byte TypeShort = 0x02;
    private const byte TypeLong = 0x03;
    private const byte TypeFloat = 0x04;
    private const byte TypeDouble = 0x05;
    private const byte TypeNull = 0x06;
    private const byte TypeLongLong = 0x08;
    private const byte TypeVarString = 0xFD;

    public static byte[] BuildExecute(uint statementId, IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var writer = new ProtocolWriter();
        writer.WriteByte(ComStmtExecute);
        writer.WriteFixedInteger(statementId, 4);
        writer.WriteByte(CursorTypeNoCursor);
        writer.WriteFixedInteger(1, 4); // iteration count

        if (values.Count == 0)
        {
            return writer.ToArray();
        }

        EncodedParameter[] encoded = new EncodedParameter[values.Count];
        byte[] nullBitmap = new byte[(values.Count + 7) / 8];
        for (int i = 0; i < values.Count; i++)
        {
            encoded[i] = Encode(values[i]);
            if (encoded[i].IsNull)
            {
                nullBitmap[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        writer.WriteBytes(nullBitmap);
        writer.WriteByte(NewParamsBound);
        foreach (EncodedParameter parameter in encoded)
        {
            writer.WriteByte(parameter.Type);
            writer.WriteByte(parameter.UnsignedFlag);
        }

        foreach (EncodedParameter parameter in encoded)
        {
            if (parameter.Value is { } value)
            {
                writer.WriteBytes(value);
            }
        }

        return writer.ToArray();
    }

    private static EncodedParameter Encode(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return new EncodedParameter(TypeNull, 0, null);
            case bool b:
                return Fixed(TypeTiny, 0, [(byte)(b ? 1 : 0)]);
            case sbyte sb:
                return Fixed(TypeTiny, 0, [unchecked((byte)sb)]);
            case byte ub:
                return Fixed(TypeTiny, UnsignedFlag, [ub]);
            case short s:
                return Fixed(TypeShort, 0, FixedInteger((ulong)(ushort)s, 2));
            case ushort us:
                return Fixed(TypeShort, UnsignedFlag, FixedInteger(us, 2));
            case int i:
                return Fixed(TypeLong, 0, FixedInteger((uint)i, 4));
            case uint ui:
                return Fixed(TypeLong, UnsignedFlag, FixedInteger(ui, 4));
            case long l:
                return Fixed(TypeLongLong, 0, FixedInteger((ulong)l, 8));
            case ulong ul:
                return Fixed(TypeLongLong, UnsignedFlag, FixedInteger(ul, 8));
            case float f:
                byte[] floatBytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(floatBytes, f);
                return Fixed(TypeFloat, 0, floatBytes);
            case double d:
                byte[] doubleBytes = new byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(doubleBytes, d);
                return Fixed(TypeDouble, 0, doubleBytes);
            case string text:
                return VarString(text);
            case byte[] bytes:
                return VarString(bytes);
            case decimal dec:
                return VarString(dec.ToString(CultureInfo.InvariantCulture));
            case DotRocksDecimal dotRocksDecimal:
                return VarString(dotRocksDecimal.ToString());
            // JSON is sent as VAR_STRING text carrying the exact raw bytes; StarRocks casts it
            // to the placeholder's JSON type, so the round trip through DotRocksJson is lossless.
            case DotRocksJson dotRocksJson:
                return VarString(dotRocksJson.RawText);
            case Int128 int128:
                return VarString(int128.ToString(CultureInfo.InvariantCulture));
            case DateTime dateTime:
                return VarString(
                    dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
                );
            case DateOnly dateOnly:
                return VarString(dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case TimeOnly timeOnly:
                return VarString(
                    timeOnly.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
                );
            case Guid guid:
                return VarString(guid.ToString("D", CultureInfo.InvariantCulture));
            default:
                throw new DotRocksUnsupportedFeatureException(
                    $"DotRocks server-prepared parameters do not support the value type '{value.GetType().Name}'."
                );
        }
    }

    private static EncodedParameter Fixed(byte type, byte unsignedFlag, byte[] value) =>
        new(type, unsignedFlag, value);

    private static EncodedParameter VarString(string text) =>
        VarString(Encoding.UTF8.GetBytes(text));

    private static EncodedParameter VarString(byte[] bytes)
    {
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedBytes(bytes);
        return new EncodedParameter(TypeVarString, 0, writer.ToArray());
    }

    private static byte[] FixedInteger(ulong value, int byteCount)
    {
        byte[] bytes = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
        {
            bytes[i] = (byte)(value >> (8 * i));
        }

        return bytes;
    }

    private readonly struct EncodedParameter(byte type, byte unsignedFlag, byte[]? value)
    {
        public byte Type { get; } = type;

        public byte UnsignedFlag { get; } = unsignedFlag;

        public byte[]? Value { get; } = value;

        public bool IsNull => Type == TypeNull;
    }
}
