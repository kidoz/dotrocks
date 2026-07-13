using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using DotRocks.Data.Protocol.Framing;

namespace DotRocks.Benchmarks;

/// <summary>
/// Benchmarks the packet framing read path (<see cref="PacketReader.ReadPayloadAsync"/>), covering
/// both a single-packet payload and a payload reassembled from many continuation packets. This is
/// the per-row allocation hot path for result streaming.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Local)]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
public class PacketFramingBenchmarks
{
    // A small chunk size frames a moderate payload across many packets so the continuation
    // reassembly path is exercised without allocating a multi-megabyte buffer.
    private const int MultiPacketChunk = 64;

    private byte[] _singlePacket = [];
    private byte[] _multiPacket = [];

    [GlobalSetup]
    public void Setup()
    {
        _singlePacket = Frame(new byte[512], MySqlPacket.MaxPacketPayloadLength);
        _multiPacket = Frame(new byte[4096], MultiPacketChunk);
    }

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public async Task<int> ReadSinglePacketPayload()
    {
        using var stream = new MemoryStream(_singlePacket);
        var reader = new PacketReader(stream);
        byte[] payload = await reader.ReadPayloadAsync().ConfigureAwait(false);
        return payload.Length;
    }

    [Benchmark]
    public int ReadSinglePacketPayloadSynchronously()
    {
        using var stream = new MemoryStream(_singlePacket);
        var reader = new PacketReader(stream);
        return reader.ReadPayload().Length;
    }

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public async Task<int> ReadMultiPacketPayload()
    {
        using var stream = new MemoryStream(_multiPacket);
        var reader = new PacketReader(stream, maxPayloadPerPacket: MultiPacketChunk);
        byte[] payload = await reader.ReadPayloadAsync().ConfigureAwait(false);
        return payload.Length;
    }

    private static byte[] Frame(byte[] payload, int maxPayloadPerPacket)
    {
        using var stream = new MemoryStream();
        var writer = new PacketWriter(stream, maxPayloadPerPacket);
        writer.ResetSequence();
        writer.WritePayloadAsync(payload, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return stream.ToArray();
    }
}
