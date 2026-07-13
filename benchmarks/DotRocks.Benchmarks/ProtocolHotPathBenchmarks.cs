using System.Diagnostics.CodeAnalysis;
using System.Text;
using BenchmarkDotNet.Attributes;
using DotRocks.Data;
using DotRocks.Data.Protocol.Commands;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Benchmarks;

/// <summary>Benchmarks protocol parsing, prepared serialization, decimal parsing, and row draining.</summary>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Local)]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
public class ProtocolHotPathBenchmarks
{
    private static readonly object?[] ParameterValues =
    [
        42L,
        "warehouse",
        12.345m,
        new DateTime(2026, 7, 13, 12, 34, 56, DateTimeKind.Unspecified),
        null,
    ];

    private byte[] _handshake = [];
    private byte[] _drainPackets = [];
    private ColumnDefinition[] _columns = [];

    [GlobalSetup]
    public void Setup()
    {
        _handshake = BuildHandshake();
        _columns =
        [
            new ColumnDefinition(
                "def",
                string.Empty,
                string.Empty,
                string.Empty,
                "value",
                "value",
                CharacterSet: 33,
                ColumnLength: 11,
                (byte)ColumnType.Long,
                Flags: 0,
                Decimals: 0
            ),
        ];

        using var stream = new MemoryStream();
        var writer = new PacketWriter(stream);
        for (int i = 0; i < 256; i++)
        {
            using var row = new ProtocolWriter();
            row.WriteLengthEncodedString(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Encoding.UTF8
            );
            writer.WritePayload(row.WrittenSpan);
        }

        writer.WritePayload([0xFE, 0x00, 0x00, 0x02, 0x00]);
        _drainPackets = stream.ToArray();
    }

    [Benchmark]
    public uint ParseHandshake() => ServerHandshake.Parse(_handshake).ConnectionId;

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public byte[] SerializePreparedParameters() =>
        BinaryParameterEncoder.BuildExecute(statementId: 7, ParameterValues);

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public DotRocksDecimal ParseDotRocksDecimal() =>
        DotRocksDecimal.Parse("123456789012345678901234567890.123456789");

    [Benchmark]
    public void DrainDiscardedRows()
    {
        using var stream = new MemoryStream(_drainPackets, writable: false);
        var rowReader = ResultRowReader.ForText(
            new PacketReader(stream),
            _columns,
            connectionId: null
        );
        rowReader.Drain();
    }

    private static byte[] BuildHandshake()
    {
        CapabilityFlags capabilities =
            CapabilityFlags.LongPassword
            | CapabilityFlags.LongFlag
            | CapabilityFlags.Protocol41
            | CapabilityFlags.SecureConnection
            | CapabilityFlags.PluginAuth;
        uint rawCapabilities = (uint)capabilities;
        using var writer = new ProtocolWriter();
        writer.WriteByte(10);
        writer.WriteNullTerminatedString("8.0.33-StarRocks-4.0.7", Encoding.ASCII);
        writer.WriteFixedInteger(42, 4);
        writer.WriteBytes([1, 2, 3, 4, 5, 6, 7, 8]);
        writer.WriteByte(0);
        writer.WriteFixedInteger(rawCapabilities & 0xFFFF, 2);
        writer.WriteByte(0x21);
        writer.WriteFixedInteger(2, 2);
        writer.WriteFixedInteger(rawCapabilities >> 16, 2);
        writer.WriteByte(21);
        writer.WriteBytes(new byte[10]);
        writer.WriteBytes([9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 0]);
        writer.WriteNullTerminatedString("mysql_native_password", Encoding.ASCII);
        return writer.ToArray();
    }
}
