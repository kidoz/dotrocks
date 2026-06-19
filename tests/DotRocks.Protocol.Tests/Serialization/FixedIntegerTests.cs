using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Serialization;

public sealed class FixedIntegerTests
{
    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(0xFFUL, 1)]
    [InlineData(0x1234UL, 2)]
    [InlineData(0x123456UL, 3)]
    [InlineData(0x12345678UL, 4)]
    [InlineData(0x1122334455667788UL, 8)]
    public void RoundTripsLittleEndian(ulong value, int byteCount)
    {
        using var writer = new ProtocolWriter();
        writer.WriteFixedInteger(value, byteCount);

        Assert.Equal(byteCount, writer.Length);

        var reader = new ProtocolReader(writer.WrittenSpan);
        ulong actual = reader.ReadFixedInteger(byteCount);

        Assert.Equal(value, actual);
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void WritesLeastSignificantByteFirst()
    {
        using var writer = new ProtocolWriter();
        writer.WriteFixedInteger(0x0102, 2);

        Assert.Equal(new byte[] { 0x02, 0x01 }, writer.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void RejectsOutOfRangeWidth(int byteCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var writer = new ProtocolWriter();
            writer.WriteFixedInteger(1, byteCount);
        });
    }

    [Fact]
    public void ReadingBeyondBufferThrows()
    {
        Assert.Throws<MalformedPacketException>(() =>
        {
            ReadOnlySpan<byte> twoBytes = [0x01, 0x02];
            var reader = new ProtocolReader(twoBytes);
            reader.ReadFixedInteger(4);
        });
    }
}
