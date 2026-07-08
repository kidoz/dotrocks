using System.Text;
using DotRocks.Data;
using DotRocks.Data.Protocol.Commands;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Commands;

public sealed class BinaryProtocolTests
{
    [Fact]
    public void BuildExecute_EncodesHeaderNullBitmapTypesAndValues()
    {
        byte[] payload = BinaryParameterEncoder.BuildExecute(7, [5L, "hello", null]);

        int offset = 0;
        Assert.Equal(0x17, payload[offset++]); // COM_STMT_EXECUTE
        Assert.Equal([0x07, 0x00, 0x00, 0x00], payload[offset..(offset + 4)]); // statement id
        offset += 4;
        Assert.Equal(0x00, payload[offset++]); // flags
        Assert.Equal([0x01, 0x00, 0x00, 0x00], payload[offset..(offset + 4)]); // iteration count
        offset += 4;

        // NULL bitmap for 3 params: only the third (index 2) is NULL -> bit 2 set.
        Assert.Equal(0x04, payload[offset++]);
        Assert.Equal(0x01, payload[offset++]); // new-params-bound flag

        // Parameter types: LONGLONG, VAR_STRING, NULL (each 2 bytes: type + unsigned flag).
        Assert.Equal([0x08, 0x00, 0xFD, 0x00, 0x06, 0x00], payload[offset..(offset + 6)]);
        offset += 6;

        // Value block: 8-byte long 5, then length-encoded "hello"; the NULL has no value bytes.
        Assert.Equal(
            [0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            payload[offset..(offset + 8)]
        );
        offset += 8;
        Assert.Equal(0x05, payload[offset++]); // length-encoded length of "hello"
        Assert.Equal("hello", Encoding.UTF8.GetString(payload[offset..(offset + 5)]));
    }

    [Fact]
    public void BuildExecute_NoParameters_OmitsBindingBlock()
    {
        byte[] payload = BinaryParameterEncoder.BuildExecute(1, []);

        // Command byte + 4-byte id + flags + 4-byte iteration count only.
        Assert.Equal(10, payload.Length);
    }

    [Fact]
    public void BuildExecute_EncodesDotRocksJsonAsVarStringWithRawText()
    {
        var json = new DotRocksJson("{\"a\":1}");

        byte[] payload = BinaryParameterEncoder.BuildExecute(1, [json]);

        int offset = 10; // command byte + statement id + flags + iteration count
        Assert.Equal(0x00, payload[offset++]); // NULL bitmap: no nulls
        Assert.Equal(0x01, payload[offset++]); // new-params-bound flag
        Assert.Equal([0xFD, 0x00], payload[offset..(offset + 2)]); // VAR_STRING, signed
        offset += 2;
        Assert.Equal(0x07, payload[offset++]); // length-encoded length of the raw text
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(payload[offset..]));
    }

    [Fact]
    public void Decode_ReadsNullBitmapAndFixedAndStringValues()
    {
        ColumnDefinition longColumn = Column(0x08); // LONGLONG
        ColumnDefinition stringColumn = Column(0xFD); // VAR_STRING
        ColumnDefinition nullColumn = Column(0x03); // LONG, but NULL via bitmap

        var row = new List<byte> { 0x00 }; // row header
        // NULL bitmap: (3 + 7 + 2)/8 = 1 byte. Column index 2 is NULL -> bit (2+2)=4 set -> 0x10.
        row.Add(0x10);
        row.AddRange([0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]); // long 42
        row.Add(0x02); // length-encoded length 2
        row.AddRange(Encoding.UTF8.GetBytes("hi"));

        object?[] values = BinaryResultRowDecoder.Decode(
            row.ToArray(),
            [longColumn, stringColumn, nullColumn]
        );

        Assert.Equal(42L, values[0]);
        Assert.Equal("hi", values[1]);
        Assert.Null(values[2]);
    }

    [Fact]
    public void Decode_YearColumn_BoxesIntMatchingGetFieldType()
    {
        ColumnDefinition yearColumn = Column(0x0D); // YEAR

        var row = new List<byte> { 0x00, 0x00 }; // header + 1-column NULL bitmap (nothing null)
        row.AddRange([0xE8, 0x07]); // 2024 little-endian

        object?[] values = BinaryResultRowDecoder.Decode(row.ToArray(), [yearColumn]);

        // The boxed runtime type must match GetFieldType (int) so consumers can unbox GetValue.
        int year = Assert.IsType<int>(values[0]);
        Assert.Equal(2024, year);
        Assert.Equal(typeof(int), ColumnTypeMapper.GetFieldType(0x0D, 4));
    }

    [Fact]
    public void Decode_DateTimeWithOutOfRangeComponents_ThrowsMalformedPacket()
    {
        ColumnDefinition dateTimeColumn = Column(0x0C); // DATETIME

        var row = new List<byte> { 0x00, 0x00 }; // header + 1-column NULL bitmap (nothing null)
        row.Add(0x07); // value length: date + time, no microseconds
        row.AddRange([0xE8, 0x07]); // year 2024
        row.Add(0x0D); // month 13 -> invalid
        row.Add(0x01); // day
        row.AddRange([0x00, 0x00, 0x00]); // hour, minute, second

        Assert.Throws<MalformedPacketException>(() =>
            BinaryResultRowDecoder.Decode(row.ToArray(), [dateTimeColumn])
        );
    }

    [Fact]
    public void Decode_TimeExceedingMaxDuration_ThrowsMalformedPacket()
    {
        ColumnDefinition timeColumn = Column(0x0B); // TIME

        var row = new List<byte> { 0x00, 0x00 }; // header + 1-column NULL bitmap
        row.Add(0x08); // value length: sign + days + hms
        row.Add(0x00); // not negative
        row.AddRange([0xFF, 0xFF, 0xFF, 0xFF]); // days far beyond TimeSpan.MaxValue
        row.AddRange([0x00, 0x00, 0x00]); // hours, minutes, seconds

        Assert.Throws<MalformedPacketException>(() =>
            BinaryResultRowDecoder.Decode(row.ToArray(), [timeColumn])
        );
    }

    [Fact]
    public void Decode_TimeMicrosecondsOverflow_ThrowsMalformedPacket()
    {
        // Regression: days just under TimeSpan.MaxValue.Days pass the day guard and the TimeSpan
        // constructor, but adding a huge microseconds field overflows via TimeSpan.operator+, which
        // throws OverflowException (not ArgumentOutOfRangeException). A malicious server row must
        // still surface as a controlled MalformedPacketException, never a raw OverflowException.
        ColumnDefinition timeColumn = Column(0x0B); // TIME

        var row = new List<byte> { 0x00, 0x00 }; // header + 1-column NULL bitmap
        row.Add(0x0C); // value length: sign + days + hms + microseconds
        row.Add(0x00); // not negative
        row.AddRange([0xFF, 0xE3, 0xA2, 0x00]); // days = 10675199 (== TimeSpan.MaxValue.Days)
        row.Add(0x02); // hours
        row.Add(0x30); // minutes = 48
        row.Add(0x05); // seconds
        row.AddRange([0xFF, 0xFF, 0xFF, 0xFF]); // microseconds: overflows the addition

        Assert.Throws<MalformedPacketException>(() =>
            BinaryResultRowDecoder.Decode(row.ToArray(), [timeColumn])
        );
    }

    private static ColumnDefinition Column(byte type) =>
        new(
            "def",
            "db",
            "t",
            "t",
            "c",
            "c",
            CharacterSet: 33,
            ColumnLength: 255,
            type,
            Flags: 0,
            Decimals: 0
        );
}
