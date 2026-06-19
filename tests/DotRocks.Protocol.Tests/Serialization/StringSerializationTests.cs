using System.Text;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Serialization;

public sealed class StringSerializationTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false
    );

    [Theory]
    [InlineData("")]
    [InlineData("starrocks")]
    [InlineData("DotRocks — 你好 \U0001F680")] // em dash, CJK, emoji
    public void LengthEncodedString_RoundTrips(string value)
    {
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedString(value, Utf8);

        var reader = new ProtocolReader(writer.WrittenSpan);
        string? actual = reader.ReadLengthEncodedString(Utf8, out bool isNull);

        Assert.False(isNull);
        Assert.Equal(value, actual);
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void LengthEncodedString_NullMarker_ReturnsNull()
    {
        ReadOnlySpan<byte> nullMarker = [ProtocolConstants.NullValueMarker];
        var reader = new ProtocolReader(nullMarker);

        string? actual = reader.ReadLengthEncodedString(Utf8, out bool isNull);

        Assert.True(isNull);
        Assert.Null(actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("information_schema")]
    [InlineData("café")]
    public void NullTerminatedString_RoundTrips(string value)
    {
        using var writer = new ProtocolWriter();
        writer.WriteNullTerminatedString(value, Utf8);

        // Last written byte must be the NUL terminator.
        Assert.Equal(0, writer.WrittenSpan[^1]);

        var reader = new ProtocolReader(writer.WrittenSpan);
        string actual = reader.ReadNullTerminatedString(Utf8);

        Assert.Equal(value, actual);
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void NullTerminatedString_WithoutTerminator_Throws()
    {
        Assert.Throws<MalformedPacketException>(() =>
        {
            ReadOnlySpan<byte> noTerminator = "abc"u8;
            var reader = new ProtocolReader(noTerminator);
            reader.ReadNullTerminatedString(Utf8);
        });
    }

    [Fact]
    public void LengthEncodedBytes_RoundTrips()
    {
        byte[] payload = [0x00, 0x01, 0xFE, 0xFF, 0x7F];
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedBytes(payload);

        var reader = new ProtocolReader(writer.WrittenSpan);
        ReadOnlySpan<byte> actual = reader.ReadLengthEncodedBytes(out bool isNull);

        Assert.False(isNull);
        Assert.True(actual.SequenceEqual(payload));
    }
}
