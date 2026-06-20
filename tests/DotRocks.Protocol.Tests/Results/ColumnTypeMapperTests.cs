using System.Text;
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
            ColumnTypeMapper.ParseTextValue((byte)ColumnType.Long, 11, bytes)
        );
    }

    [Fact]
    public void ParseTextValue_OverflowingInteger_ThrowsMalformedPacketException()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("99999999999999999999999999");

        Assert.Throws<MalformedPacketException>(() =>
            ColumnTypeMapper.ParseTextValue((byte)ColumnType.Long, 11, bytes)
        );
    }

    [Fact]
    public void ParseTextValue_BitColumn_ReturnsRawBytes()
    {
        byte[] bytes = [0x00, 0xFF, 0x10];

        object value = ColumnTypeMapper.ParseTextValue((byte)ColumnType.Bit, 0, bytes);

        byte[] result = Assert.IsType<byte[]>(value);
        Assert.Equal(bytes, result);
    }

    [Fact]
    public void GetFieldType_BitColumn_IsByteArray()
    {
        Assert.Equal(typeof(byte[]), ColumnTypeMapper.GetFieldType((byte)ColumnType.Bit, 0));
    }

    [Fact]
    public void GetFieldType_DistinguishesBooleanTinyintSmallint()
    {
        // StarRocks sends BOOLEAN as TINYINT with display length 1, and a wider TINYINT with 4.
        Assert.Equal(
            typeof(bool),
            ColumnTypeMapper.GetFieldType((byte)ColumnType.Tiny, BooleanLength)
        );
        Assert.Equal(
            typeof(sbyte),
            ColumnTypeMapper.GetFieldType((byte)ColumnType.Tiny, TinyIntLength)
        );
        Assert.Equal(
            typeof(short),
            ColumnTypeMapper.GetFieldType((byte)ColumnType.Short, SmallIntLength)
        );
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void ParseTextValue_BooleanColumn_ReturnsBool(string text, bool expected)
    {
        object value = ColumnTypeMapper.ParseTextValue(
            (byte)ColumnType.Tiny,
            BooleanLength,
            Encoding.UTF8.GetBytes(text)
        );

        Assert.Equal(expected, Assert.IsType<bool>(value));
    }

    [Fact]
    public void ParseTextValue_TinyintColumn_ReturnsSByte()
    {
        object value = ColumnTypeMapper.ParseTextValue(
            (byte)ColumnType.Tiny,
            TinyIntLength,
            Encoding.UTF8.GetBytes("-42")
        );

        Assert.Equal((sbyte)-42, Assert.IsType<sbyte>(value));
    }

    [Fact]
    public void ParseTextValue_SmallintColumn_ReturnsShort()
    {
        object value = ColumnTypeMapper.ParseTextValue(
            (byte)ColumnType.Short,
            SmallIntLength,
            Encoding.UTF8.GetBytes("1234")
        );

        Assert.Equal((short)1234, Assert.IsType<short>(value));
    }
}
