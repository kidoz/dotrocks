using System.Text;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Results;

public sealed class ColumnTypeMapperTests
{
    [Fact]
    public void ParseTextValue_TypeMismatch_ThrowsMalformedPacketException()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("not-a-number");

        Assert.Throws<MalformedPacketException>(() =>
            ColumnTypeMapper.ParseTextValue((byte)ColumnType.Long, bytes)
        );
    }

    [Fact]
    public void ParseTextValue_OverflowingInteger_ThrowsMalformedPacketException()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("99999999999999999999999999");

        Assert.Throws<MalformedPacketException>(() =>
            ColumnTypeMapper.ParseTextValue((byte)ColumnType.Long, bytes)
        );
    }

    [Fact]
    public void ParseTextValue_BitColumn_ReturnsRawBytes()
    {
        byte[] bytes = [0x00, 0xFF, 0x10];

        object value = ColumnTypeMapper.ParseTextValue((byte)ColumnType.Bit, bytes);

        byte[] result = Assert.IsType<byte[]>(value);
        Assert.Equal(bytes, result);
    }

    [Fact]
    public void GetFieldType_BitColumn_IsByteArray()
    {
        Assert.Equal(typeof(byte[]), ColumnTypeMapper.GetFieldType((byte)ColumnType.Bit));
    }
}
