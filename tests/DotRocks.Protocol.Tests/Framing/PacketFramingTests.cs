using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Framing;

public sealed class PacketFramingTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 5)]
    [InlineData(0xFFFFFF, 255)]
    public void Header_RoundTrips(int payloadLength, byte sequenceId)
    {
        Span<byte> buffer = stackalloc byte[MySqlPacket.HeaderLength];
        new PacketHeader(payloadLength, sequenceId).WriteTo(buffer);

        PacketHeader parsed = PacketHeader.Parse(buffer);

        Assert.Equal(payloadLength, parsed.PayloadLength);
        Assert.Equal(sequenceId, parsed.SequenceId);
    }

    [Fact]
    public void Header_RejectsOversizePayload()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PacketHeader(MySqlPacket.MaxPacketPayloadLength + 1, 0)
        );
    }

    [Fact]
    public async Task SmallPayload_WritesSinglePacket_WithIncrementingSequence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] payload = [0x01, 0x02, 0x03];
        using var stream = new MemoryStream();

        var writer = new PacketWriter(stream);
        await writer.WritePayloadAsync(payload, ct);

        byte[] framed = stream.ToArray();
        Assert.Equal(MySqlPacket.HeaderLength + payload.Length, framed.Length);
        Assert.Equal(payload.Length, framed[0]);
        Assert.Equal(0, framed[3]); // sequence id
        Assert.Equal(1, writer.SequenceId); // advanced after the write
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)] // exact multiple of the test packet limit -> trailing empty packet
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(64)]
    public async Task Payloads_RoundTrip_AcrossPacketBoundaries(int length)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Small per-packet limit forces multi-packet behavior without huge buffers.
        const int maxPerPacket = 4;
        byte[] payload = new byte[length];
        for (int i = 0; i < length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        using var stream = new MemoryStream();
        var writer = new PacketWriter(stream, maxPerPacket);
        await writer.WritePayloadAsync(payload, ct);

        stream.Position = 0;
        var reader = new PacketReader(stream, maxPerPacket);
        byte[] readBack = await reader.ReadPayloadAsync(ct);

        Assert.Equal(payload, readBack);
    }

    [Fact]
    public async Task ExactMultiple_EmitsTrailingEmptyPacket()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int maxPerPacket = 4;
        byte[] payload = [10, 20, 30, 40]; // exactly one full packet
        using var stream = new MemoryStream();

        await new PacketWriter(stream, maxPerPacket).WritePayloadAsync(payload, ct);

        byte[] framed = stream.ToArray();
        // full packet (4 header + 4 data) followed by an empty terminating packet (4 header).
        Assert.Equal(MySqlPacket.HeaderLength * 2 + payload.Length, framed.Length);
    }

    [Fact]
    public async Task OutOfOrderSequenceId_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        // Header announces 1 payload byte but with sequence id 7 instead of the expected 0.
        stream.Write([0x01, 0x00, 0x00, 0x07, 0xAA]);
        stream.Position = 0;

        var reader = new PacketReader(stream);
        await Assert.ThrowsAsync<MalformedPacketException>(async () =>
            await reader.ReadPayloadAsync(ct)
        );
    }

    [Fact]
    public async Task TruncatedPayload_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        // Header claims 4 payload bytes, but only 2 follow.
        stream.Write([0x04, 0x00, 0x00, 0x00, 0xAA, 0xBB]);
        stream.Position = 0;

        var reader = new PacketReader(stream);
        await Assert.ThrowsAsync<MalformedPacketException>(async () =>
            await reader.ReadPayloadAsync(ct)
        );
    }

    [Fact]
    public async Task PayloadOverConfiguredLogicalLimit_ThrowsBeforeAllocation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        // One full continuation packet followed by another packet would exceed the 7-byte limit.
        stream.Write([
            0x04,
            0x00,
            0x00,
            0x00,
            0x01,
            0x02,
            0x03,
            0x04,
            0x04,
            0x00,
            0x00,
            0x01,
            0x05,
            0x06,
            0x07,
            0x08,
        ]);
        stream.Position = 0;

        var reader = new PacketReader(stream, maxPayloadPerPacket: 4, maxLogicalPayloadLength: 7);

        await Assert.ThrowsAsync<MalformedPacketException>(async () =>
            await reader.ReadPayloadAsync(ct)
        );
    }

    [Fact]
    public void NegativeLogicalPayloadLimit_Throws()
    {
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PacketReader(stream, maxLogicalPayloadLength: -1)
        );
    }

    [Fact]
    public async Task SequenceId_WrapsPast255()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        var writer = new PacketWriter(stream);
        byte[] oneByte = [0x01];

        // 256 single-packet writes advance the sequence id through a full byte and back to 0.
        for (int i = 0; i < 256; i++)
        {
            await writer.WritePayloadAsync(oneByte, ct);
        }

        Assert.Equal(0, writer.SequenceId);
    }
}
