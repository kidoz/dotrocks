using System.Text;
using DotRocks.Data.Protocol.Commands;
using DotRocks.Data.Protocol.Results;
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
