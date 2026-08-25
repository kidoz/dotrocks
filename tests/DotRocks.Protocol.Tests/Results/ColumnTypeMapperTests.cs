using System.Text;
using DotRocks.Data;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Results;

public sealed class ColumnTypeMapperTests
{
    private const uint TinyIntLength = 4;
    private const uint BooleanLength = 1;
    private const uint SmallIntLength = 6;

    [Fact]
    public void ParseTextValue_TypeMismatch_ThrowsMalformedPacketException()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("not-a-number");

        Assert.Throws<MalformedPacketException>(() =>
            ColumnTypeMapper.ParseTextValue(Column((byte)ColumnType.Long, 11), bytes)
        );
    }

    [Fact]
    public void ParseTextValue_OverflowingInteger_ThrowsMalformedPacketException()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("99999999999999999999999999");

        Assert.Throws<MalformedPacketException>(() =>
            ColumnTypeMapper.ParseTextValue(Column((byte)ColumnType.Long, 11), bytes)
        );
    }

    [Fact]
    public void ParseTextValue_BitColumn_ReturnsRawBytes()
    {
        byte[] bytes = [0x00, 0xFF, 0x10];

        object value = ColumnTypeMapper.ParseTextValue(Column((byte)ColumnType.Bit, 0), bytes);

        byte[] result = Assert.IsType<byte[]>(value);
        Assert.Equal(bytes, result);
    }

    [Fact]
    public void GetFieldType_BitColumn_IsByteArray()
    {
        Assert.Equal(
            typeof(byte[]),
            ColumnTypeMapper.GetFieldType(Column((byte)ColumnType.Bit, 0))
        );
    }

    [Fact]
    public void GetFieldType_DistinguishesBooleanTinyintSmallint()
    {
        // StarRocks sends BOOLEAN as TINYINT with display length 1, and a wider TINYINT with 4.
        Assert.Equal(
            typeof(bool),
            ColumnTypeMapper.GetFieldType(Column((byte)ColumnType.Tiny, BooleanLength))
        );
        Assert.Equal(
            typeof(sbyte),
            ColumnTypeMapper.GetFieldType(Column((byte)ColumnType.Tiny, TinyIntLength))
        );
        Assert.Equal(
            typeof(short),
            ColumnTypeMapper.GetFieldType(Column((byte)ColumnType.Short, SmallIntLength))
        );
    }

    // StarRocks reports a DECIMAL's wire column length as precision + 3, plus one more when the
    // scale is non-zero (verified live; see FitsInSystemDecimal). Precisions up to 28 always
    // convert to System.Decimal exactly, so they materialize as decimal; wider ones as
    // DotRocksDecimal.
    [Theory]
    [InlineData(32u, (byte)12)] // decimal(28, 12)
    [InlineData(14u, (byte)4)] // decimal(10, 4)
    [InlineData(31u, (byte)0)] // decimal(28, 0)
    [InlineData(22u, (byte)18)] // decimal(18, 18)
    public void GetFieldType_DecimalWithinSystemDecimalPrecision_IsDecimal(
        uint columnLength,
        byte decimals
    )
    {
        Assert.Equal(
            typeof(decimal),
            ColumnTypeMapper.GetFieldType(
                Column((byte)ColumnType.NewDecimal, columnLength, decimals: decimals)
            )
        );
    }

    [Theory]
    [InlineData(33u, (byte)2)] // decimal(29, 2)
    [InlineData(42u, (byte)4)] // decimal(38, 4)
    [InlineData(32u, (byte)0)] // decimal(29, 0)
    public void GetFieldType_DecimalBeyondSystemDecimalPrecision_IsDotRocksDecimal(
        uint columnLength,
        byte decimals
    )
    {
        Assert.Equal(
            typeof(DotRocksDecimal),
            ColumnTypeMapper.GetFieldType(
                Column((byte)ColumnType.NewDecimal, columnLength, decimals: decimals)
            )
        );
    }

    [Fact]
    public void ParseTextValue_DecimalWithinSystemDecimalPrecision_ReturnsDecimal()
    {
        object value = ColumnTypeMapper.ParseTextValue(
            Column((byte)ColumnType.NewDecimal, 32, decimals: 12),
            Encoding.UTF8.GetBytes("-12.340000000000")
        );

        Assert.Equal(-12.340000000000m, Assert.IsType<decimal>(value));
    }

    [Fact]
    public void ParseTextValue_DecimalBeyondSystemDecimalPrecision_ReturnsDotRocksDecimal()
    {
        object value = ColumnTypeMapper.ParseTextValue(
            Column((byte)ColumnType.NewDecimal, 42, decimals: 4),
            Encoding.UTF8.GetBytes("1234567890123456789012345678901234.5678")
        );

        Assert.Equal(
            DotRocksDecimal.Parse("1234567890123456789012345678901234.5678"),
            Assert.IsType<DotRocksDecimal>(value)
        );
    }

    [Fact]
    public void ParseTextValue_MalformedNarrowDecimal_ThrowsMalformedPacketException()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("not-a-decimal");

        Assert.Throws<MalformedPacketException>(() =>
            ColumnTypeMapper.ParseTextValue(
                Column((byte)ColumnType.NewDecimal, 32, decimals: 12),
                bytes
            )
        );
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void ParseTextValue_BooleanColumn_ReturnsBool(string text, bool expected)
    {
        object value = ColumnTypeMapper.ParseTextValue(
            Column((byte)ColumnType.Tiny, BooleanLength),
            Encoding.UTF8.GetBytes(text)
        );

        Assert.Equal(expected, Assert.IsType<bool>(value));
    }

    [Fact]
    public void ParseTextValue_TinyintColumn_ReturnsSByte()
    {
        object value = ColumnTypeMapper.ParseTextValue(
            Column((byte)ColumnType.Tiny, TinyIntLength),
            Encoding.UTF8.GetBytes("-42")
        );

        Assert.Equal((sbyte)-42, Assert.IsType<sbyte>(value));
    }

    [Fact]
    public void ParseTextValue_SmallintColumn_ReturnsShort()
    {
        object value = ColumnTypeMapper.ParseTextValue(
            Column((byte)ColumnType.Short, SmallIntLength),
            Encoding.UTF8.GetBytes("1234")
        );

        Assert.Equal((short)1234, Assert.IsType<short>(value));
    }

    public static TheoryData<string, TimeSpan> TimeValues =>
        new()
        {
            { "00:00:00", TimeSpan.Zero },
            { "13:14:15", new TimeSpan(13, 14, 15) },
            { "1:02:03", new TimeSpan(1, 2, 3) },
            // The MySQL TIME convention is a duration: hours can exceed 23 (e.g. timediff()).
            { "48:00:00", TimeSpan.FromHours(48) },
            { "838:59:59", new TimeSpan(0, 838, 59, 59) },
            { "-01:30:00", new TimeSpan(1, 30, 0).Negate() },
            { "-838:59:59", new TimeSpan(0, 838, 59, 59).Negate() },
            // Fractional seconds are left-aligned microseconds (".25" is 250 ms).
            { "13:14:15.500000", new TimeSpan(13, 14, 15) + TimeSpan.FromMilliseconds(500) },
            { "10:00:00.25", TimeSpan.FromHours(10) + TimeSpan.FromMilliseconds(250) },
            { "00:00:00.000001", TimeSpan.FromTicks(10) },
            { "-00:00:00.250000", TimeSpan.FromMilliseconds(-250) },
        };

    [Theory]
    [MemberData(nameof(TimeValues))]
    public void ParseTextValue_TimeColumn_ParsesMySqlDurationText(string text, TimeSpan expected)
    {
        object value = ColumnTypeMapper.ParseTextValue(
            Column((byte)ColumnType.Time, 0),
            Encoding.UTF8.GetBytes(text)
        );

        Assert.Equal(expected, Assert.IsType<TimeSpan>(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("aa:bb:cc")]
    [InlineData("12:34")]
    [InlineData("12:60:00")]
    [InlineData("12:00:60")]
    [InlineData("839:00:00")]
    [InlineData("12:00:00.")]
    [InlineData("12:00:00.1234567")]
    [InlineData("12:00:00x")]
    [InlineData(":00:00")]
    public void ParseTextValue_MalformedTime_ThrowsMalformedPacketException(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        Assert.Throws<MalformedPacketException>(() =>
            ColumnTypeMapper.ParseTextValue(Column((byte)ColumnType.Time, 0), bytes)
        );
    }

    private static ColumnDefinition Column(
        byte columnType,
        uint columnLength,
        ushort flags = 0,
        byte decimals = 0
    ) =>
        new(
            "def",
            string.Empty,
            string.Empty,
            string.Empty,
            "c",
            "c",
            CharacterSet: 33,
            columnLength,
            columnType,
            flags,
            decimals
        );
}
