using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using DotRocks.Data;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Benchmarks;

/// <summary>
/// Measures end-to-end row materialization — packet framing, text decoding, and the typed
/// <see cref="DotRocksDataReader"/> accessors — over an in-memory result set, so per-row cost and
/// allocation are guarded by the performance budget without needing a live server. The
/// server-backed <see cref="LargeResultStreamingBenchmarks"/> covers real network behavior but is
/// excluded from the budget gate, which left the read path's per-row allocation ungoverned.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Local)]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
public class ResultMaterializationBenchmarks
{
    /// <summary>
    /// Rows per operation. The budget divided by this count is the effective per-row allocation
    /// ceiling, so changing it requires re-deriving the budget.
    /// </summary>
    public const int RowCount = 256;

    private byte[] _resultSetPackets = [];
    private ColumnDefinition[] _columns = [];

    [GlobalSetup]
    public void Setup()
    {
        _columns =
        [
            Column("id", ColumnType.LongLong, length: 20),
            Column("name", ColumnType.VarString, length: 64),
            Column("amount", ColumnType.NewDecimal, length: 18, decimals: 4),
        ];

        using var stream = new MemoryStream();
        var writer = new PacketWriter(stream);
        for (int i = 0; i < RowCount; i++)
        {
            using var row = new ProtocolWriter();
            row.WriteLengthEncodedString(i.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
            row.WriteLengthEncodedString(
                string.Create(CultureInfo.InvariantCulture, $"warehouse-{i}"),
                Encoding.UTF8
            );
            row.WriteLengthEncodedString(
                string.Create(CultureInfo.InvariantCulture, $"{i}.{i % 10000:D4}"),
                Encoding.UTF8
            );
            writer.WritePayload(row.WrittenSpan);
        }

        writer.WritePayload([0xFE, 0x00, 0x00, 0x02, 0x00]);
        _resultSetPackets = stream.ToArray();
    }

    /// <summary>
    /// Reads every row through the public reader surface with typed accessors, which is what a
    /// consumer scanning a large result set actually pays per row.
    /// </summary>
    [Benchmark]
    public long MaterializeRows()
    {
        using var stream = new MemoryStream(_resultSetPackets, writable: false);
        ResultRowReader rowReader = ResultRowReader.ForText(
            new PacketReader(stream),
            _columns,
            connectionId: null
        );
        using var reader = new DotRocksDataReader(
            StreamingQueryResult.FromRows(_columns, rowReader)
        );

        long checksum = 0;
        while (reader.Read())
        {
            checksum += reader.GetInt64(0);
            checksum += reader.GetString(1).Length;
            checksum += (long)reader.GetDecimal(2);
        }

        return checksum;
    }

    private static ColumnDefinition Column(
        string name,
        ColumnType type,
        uint length,
        byte decimals = 0
    ) =>
        new(
            "def",
            string.Empty,
            string.Empty,
            string.Empty,
            name,
            name,
            CharacterSet: 33,
            ColumnLength: length,
            (byte)type,
            Flags: 0,
            decimals
        );
}
