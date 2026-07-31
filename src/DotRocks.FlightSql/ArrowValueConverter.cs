using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Types;
using DotRocks.Data;

namespace DotRocks.FlightSql;

internal static class ArrowValueConverter
{
    public static object GetValue(IArrowArray array, int index)
    {
        if (array.IsNull(index))
        {
            return DBNull.Value;
        }

        return array switch
        {
            NullArray => DBNull.Value,
            BooleanArray value => value.GetValue(index)!.Value,
            Int8Array value => value.GetValue(index)!.Value,
            UInt8Array value => value.GetValue(index)!.Value,
            Int16Array value => value.GetValue(index)!.Value,
            UInt16Array value => value.GetValue(index)!.Value,
            Int32Array value => value.GetValue(index)!.Value,
            UInt32Array value => value.GetValue(index)!.Value,
            Int64Array value => value.GetValue(index)!.Value,
            UInt64Array value => value.GetValue(index)!.Value,
            HalfFloatArray value => value.GetValue(index)!.Value,
            FloatArray value => value.GetValue(index)!.Value,
            DoubleArray value => value.GetValue(index)!.Value,
            StringArray value => value.GetString(index),
            LargeStringArray value => value.GetString(index),
            StringViewArray value => value.GetString(index),
            BinaryArray value => value.GetBytes(index).ToArray(),
            LargeBinaryArray value => value.GetBytes(index).ToArray(),
            BinaryViewArray value => value.GetBytes(index).ToArray(),
            Decimal32Array value => ParseDecimal(value.GetString(index)),
            Decimal64Array value => ParseDecimal(value.GetString(index)),
            Decimal128Array value => ParseDecimal(value.GetString(index)),
            Decimal256Array value => ParseDecimal(value.GetString(index)!),
            FixedSizeBinaryArray value => value.GetBytes(index).ToArray(),
            Date32Array value => value.GetDateOnly(index)!.Value,
            Date64Array value => value.GetDateTime(index)!.Value,
            TimestampArray value => value.GetTimestamp(index)!.Value.DateTime,
            TimestampWithOffsetArray value => value.GetValue(index)!.Value,
            Time32Array value => value.GetTime(index)!.Value,
            Time64Array value => value.GetTime(index)!.Value,
            DurationArray value => value.GetTimeSpan(index)!.Value,
            MapArray value => GetMap(value, index),
            ListArray value => GetList(
                value.Values,
                value.ValueOffsets[index],
                value.GetValueLength(index)
            ),
            LargeListArray value => GetList(
                value.Values,
                checked((int)value.ValueOffsets[index]),
                checked((int)value.GetValueLength(index))
            ),
            FixedSizeListArray value => GetFixedSizeList(value, index),
            StructArray value => GetStruct(value, index),
            DictionaryArray value => GetDictionaryValue(value, index),
            _ => throw new NotSupportedException(
                $"Arrow type '{array.Data.DataType.Name}' is not supported by the ADO.NET reader."
            ),
        };
    }

    public static Type GetFieldType(IArrowType dataType) =>
        dataType is TimestampWithOffsetType
            ? typeof(DateTimeOffset)
            : dataType.TypeId switch
            {
                ArrowTypeId.Boolean => typeof(bool),
                ArrowTypeId.Int8 => typeof(sbyte),
                ArrowTypeId.UInt8 => typeof(byte),
                ArrowTypeId.Int16 => typeof(short),
                ArrowTypeId.UInt16 => typeof(ushort),
                ArrowTypeId.Int32 => typeof(int),
                ArrowTypeId.UInt32 => typeof(uint),
                ArrowTypeId.Int64 => typeof(long),
                ArrowTypeId.UInt64 => typeof(ulong),
                ArrowTypeId.HalfFloat => typeof(Half),
                ArrowTypeId.Float => typeof(float),
                ArrowTypeId.Double => typeof(double),
                ArrowTypeId.String or ArrowTypeId.LargeString or ArrowTypeId.StringView =>
                    typeof(string),
                ArrowTypeId.Binary
                or ArrowTypeId.LargeBinary
                or ArrowTypeId.BinaryView
                or ArrowTypeId.FixedSizedBinary => typeof(byte[]),
                ArrowTypeId.Date32 => typeof(DateOnly),
                ArrowTypeId.Date64 or ArrowTypeId.Timestamp => typeof(DateTime),
                ArrowTypeId.Time32 or ArrowTypeId.Time64 => typeof(TimeOnly),
                ArrowTypeId.Duration => typeof(TimeSpan),
                ArrowTypeId.List or ArrowTypeId.LargeList or ArrowTypeId.FixedSizeList =>
                    typeof(object[]),
                ArrowTypeId.Map => typeof(KeyValuePair<object, object?>[]),
                ArrowTypeId.Struct => typeof(IReadOnlyDictionary<string, object?>),
                ArrowTypeId.Dictionary when dataType is DictionaryType dictionary => GetFieldType(
                    dictionary.ValueType
                ),
                ArrowTypeId.Decimal32
                or ArrowTypeId.Decimal64
                or ArrowTypeId.Decimal128
                or ArrowTypeId.Decimal256 => typeof(decimal),
                _ => typeof(object),
            };

    private static object ParseDecimal(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : DotRocksDecimal.Parse(text);

    private static object?[] GetList(IArrowArray values, int offset, int length)
    {
        var result = new object?[length];
        for (int index = 0; index < length; index++)
        {
            result[index] = NormalizeNestedValue(GetValue(values, offset + index));
        }

        return result;
    }

    private static KeyValuePair<object, object?>[] GetMap(MapArray array, int index)
    {
        int offset = array.ValueOffsets[index];
        int length = array.GetValueLength(index);
        var result = new KeyValuePair<object, object?>[length];
        for (int itemIndex = 0; itemIndex < length; itemIndex++)
        {
            object key =
                NormalizeNestedValue(GetValue(array.Keys, offset + itemIndex))
                ?? throw new InvalidOperationException("Arrow map keys cannot be null.");
            object? value = NormalizeNestedValue(GetValue(array.Values, offset + itemIndex));
            result[itemIndex] = new KeyValuePair<object, object?>(key, value);
        }

        return result;
    }

    private static object?[] GetFixedSizeList(FixedSizeListArray array, int index)
    {
        int length = ((FixedSizeListType)array.Data.DataType).ListSize;
        int offset = checked((array.Offset + index) * length);
        return GetList(array.Values, offset, length);
    }

    private static Dictionary<string, object?> GetStruct(StructArray array, int index)
    {
        var type = (StructType)array.Data.DataType;
        var result = new Dictionary<string, object?>(type.Fields.Count, StringComparer.Ordinal);
        for (int fieldIndex = 0; fieldIndex < type.Fields.Count; fieldIndex++)
        {
            result.Add(
                type.Fields[fieldIndex].Name,
                NormalizeNestedValue(GetValue(array.Fields[fieldIndex], index))
            );
        }

        return result;
    }

    private static object GetDictionaryValue(DictionaryArray array, int index)
    {
        object physicalIndex = GetValue(array.Indices, index);
        int dictionaryIndex = Convert.ToInt32(physicalIndex, CultureInfo.InvariantCulture);
        return GetValue(array.Dictionary, dictionaryIndex);
    }

    private static object? NormalizeNestedValue(object value) => value is DBNull ? null : value;
}
