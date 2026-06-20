using System.Diagnostics.CodeAnalysis;
using System.Text;
using BenchmarkDotNet.Attributes;
using DotRocks.Data.Protocol.Commands;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Benchmarks;

/// <summary>
/// Benchmarks the protocol serialization and value-parsing hot paths.
/// </summary>
[MemoryDiagnoser]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
public class SerializationBenchmarks
{
    private static readonly byte[] IntegerText = Encoding.UTF8.GetBytes("1234567");
    private static readonly byte[] StringText = Encoding.UTF8.GetBytes("the quick brown fox");

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public byte[] WriteLengthEncodedRow()
    {
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedInteger(3);
        writer.WriteLengthEncodedString("warehouse", Encoding.UTF8);
        writer.WriteLengthEncodedString("events", Encoding.UTF8);
        writer.WriteLengthEncodedString("the quick brown fox", Encoding.UTF8);
        return writer.ToArray();
    }

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public string? ReadLengthEncodedString()
    {
        using var writer = new ProtocolWriter();
        writer.WriteLengthEncodedString("the quick brown fox", Encoding.UTF8);
        var reader = new ProtocolReader(writer.ToArray());
        return reader.ReadLengthEncodedString(Encoding.UTF8, out _);
    }

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public object ParseIntegerValue() =>
        ColumnTypeMapper.ParseTextValue((byte)ColumnType.Long, IntegerText);

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public object ParseStringValue() =>
        ColumnTypeMapper.ParseTextValue((byte)ColumnType.VarString, StringText);

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public string FormatSqlLiteral() => SqlLiteralFormatter.Format("O'Brien \\ \"value\"");
}
