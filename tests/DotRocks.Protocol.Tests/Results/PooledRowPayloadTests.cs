using System.Buffers;
using System.Text;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Results;

/// <summary>
/// The row loop reads each row into a buffer rented from <see cref="ArrayPool{T}"/> and returns it
/// as soon as the row is decoded, which is only safe because every decoded value copies out of the
/// payload. These tests pin that invariant: if a decoder ever returned a value that aliased the
/// payload, previously read rows would change when the buffer is reused.
/// </summary>
public sealed class PooledRowPayloadTests
{
    [Fact]
    public void DecodedValues_SurviveReuseOfThePooledPayloadBuffer()
    {
        // Two rows of identical size, so the pool hands the second read the buffer the first read
        // returned — the exact condition that would expose aliasing.
        byte[] packets = BuildTextResultSet("alpha", "bravo");
        using var stream = new MemoryStream(packets, writable: false);
        ResultRowReader rowReader = ResultRowReader.ForText(
            new PacketReader(stream),
            [TextColumn("value")],
            connectionId: null
        );

        object?[] firstRow = rowReader.ReadRow()!;
        Assert.Equal("alpha", firstRow[0]);

        // Poison a buffer of the same size before reading the next row: if the decoded string
        // pointed into the pooled array, this would corrupt the value already handed out.
        byte[] poisoned = ArrayPool<byte>.Shared.Rent(packets.Length);
        Array.Fill(poisoned, (byte)0xFF);
        ArrayPool<byte>.Shared.Return(poisoned);

        object?[] secondRow = rowReader.ReadRow()!;

        Assert.Equal("bravo", secondRow[0]);
        Assert.Equal("alpha", firstRow[0]);
        Assert.Null(rowReader.ReadRow());
    }

    [Fact]
    public void BinaryValues_AreCopiedOutOfThePooledPayload()
    {
        byte[] blob = [0x01, 0x02, 0x03, 0x04];
        using var stream = new MemoryStream(BuildBlobResultSet(blob), writable: false);
        ResultRowReader rowReader = ResultRowReader.ForText(
            new PacketReader(stream),
            [BlobColumn("data")],
            connectionId: null
        );

        object?[] row = rowReader.ReadRow()!;
        byte[] value = Assert.IsType<byte[]>(row[0]);

        byte[] poisoned = ArrayPool<byte>.Shared.Rent(64);
        Array.Fill(poisoned, (byte)0xFF);
        ArrayPool<byte>.Shared.Return(poisoned);

        Assert.Equal(blob, value);
    }

    private static byte[] BuildTextResultSet(params string[] rows)
    {
        using var stream = new MemoryStream();
        var writer = new PacketWriter(stream);
        foreach (string row in rows)
        {
            using var payload = new ProtocolWriter();
            payload.WriteLengthEncodedString(row, Encoding.UTF8);
            writer.WritePayload(payload.WrittenSpan);
        }

        writer.WritePayload([0xFE, 0x00, 0x00, 0x02, 0x00]);
        return stream.ToArray();
    }

    private static byte[] BuildBlobResultSet(byte[] value)
    {
        using var stream = new MemoryStream();
        var writer = new PacketWriter(stream);
        using (var payload = new ProtocolWriter())
        {
            payload.WriteLengthEncodedBytes(value);
            writer.WritePayload(payload.WrittenSpan);
        }

        writer.WritePayload([0xFE, 0x00, 0x00, 0x02, 0x00]);
        return stream.ToArray();
    }

    private static ColumnDefinition TextColumn(string name) =>
        Column(name, (byte)ColumnType.VarString);

    private static ColumnDefinition BlobColumn(string name) => Column(name, (byte)ColumnType.Blob);

    private static ColumnDefinition Column(string name, byte type) =>
        new(
            "def",
            string.Empty,
            string.Empty,
            string.Empty,
            name,
            name,
            CharacterSet: 33,
            ColumnLength: 64,
            type,
            Flags: 0,
            Decimals: 0
        );
}
