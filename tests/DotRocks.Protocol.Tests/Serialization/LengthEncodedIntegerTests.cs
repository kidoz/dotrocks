using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Serialization;

public sealed class LengthEncodedIntegerTests
{
    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 1)]
    [InlineData(250UL, 1)] // largest single-byte value (< 0xFB)
    [InlineData(251UL, 3)] // first value needing the 0xFC + 2-byte form
    [InlineData(65535UL, 3)]
    [InlineData(65536UL, 4)] // 0xFD + 3-byte form
    [InlineData(16777215UL, 4)]
    [InlineData(16777216UL, 9)] // 0xFE + 8-byte form
    [InlineData(ulong.MaxValue, 9)]
    public void RoundTrips_WithExpectedEncodedLength(ulong value, int expectedEncodedLength)
    {
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedInteger(value);

        Assert.Equal(expectedEncodedLength, writer.Length);

        var reader = new ProtocolReader(writer.WrittenSpan);
        ulong actual = reader.ReadLengthEncodedInteger();

        Assert.Equal(value, actual);
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void NullMarker_IsReportedAsNull()
    {
        ReadOnlySpan<byte> nullMarker = [ProtocolConstants.NullValueMarker];
        var reader = new ProtocolReader(nullMarker);

        ulong value = reader.ReadLengthEncodedInteger(out bool isNull);

        Assert.True(isNull);
        Assert.Equal(0UL, value);
    }

    [Fact]
    public void NonNullOverload_Throws_OnNullMarker()
    {
        Assert.Throws<MalformedPacketException>(() =>
        {
            ReadOnlySpan<byte> nullMarker = [ProtocolConstants.NullValueMarker];
            var reader = new ProtocolReader(nullMarker);
            reader.ReadLengthEncodedInteger();
        });
    }

    [Fact]
    public void ErrorPacketHeaderPrefix_IsRejected()
    {
        Assert.Throws<MalformedPacketException>(() =>
        {
            ReadOnlySpan<byte> invalid = [ProtocolConstants.ErrorPacketHeader];
            var reader = new ProtocolReader(invalid);
            reader.ReadLengthEncodedInteger(out _);
        });
    }

    [Fact]
    public void TruncatedMultiByteValue_Throws()
    {
        // 0xFC announces a 2-byte integer, but only one byte follows.
        Assert.Throws<MalformedPacketException>(() =>
        {
            ReadOnlySpan<byte> truncated = [ProtocolConstants.LengthEncodedTwoBytePrefix, 0x01];
            var reader = new ProtocolReader(truncated);
            reader.ReadLengthEncodedInteger();
        });
    }
}
