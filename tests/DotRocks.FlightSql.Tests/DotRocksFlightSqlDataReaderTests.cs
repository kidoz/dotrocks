using System.Diagnostics.CodeAnalysis;
using Apache.Arrow;
using Apache.Arrow.Types;
using DotRocks.FlightSql;
using Xunit;

namespace DotRocks.FlightSql.Tests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "Await-using declarations in xUnit tests intentionally retain the test context."
)]
public sealed class DotRocksFlightSqlDataReaderTests
{
    [Fact]
    public async Task ReadAsync_MaterializesPrimitiveValuesAndNulls()
    {
        var schema = new Schema(
            [
                new Field("enabled", BooleanType.Default, false),
                new Field("name", StringType.Default, true),
                new Field("payload", BinaryType.Default, false),
            ],
            null
        );
        using var enabled = new BooleanArray.Builder().Append(true).Append(false).Build();
        using var names = new StringArray.Builder().Append("alpha").AppendNull().Build();
        using var payloads = new BinaryArray.Builder().Append([1, 2]).Append([3]).Build();
        using var batch = new RecordBatch(schema, [enabled, names, payloads], 2);
        var result = new DotRocksFlightSqlResult(schema, 2, -1, true, _ => SingleBatch(batch));
        await using var reader = new DotRocksFlightSqlDataReader(result);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("alpha", reader.GetString(1));
        Assert.Equal(
            [1, 2],
            await reader.GetFieldValueAsync<byte[]>(2, cancellationToken).ConfigureAwait(true)
        );

        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        Assert.False(reader.GetBoolean(0));
        Assert.True(await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            [3],
            await reader.GetFieldValueAsync<byte[]>(2, cancellationToken).ConfigureAwait(true)
        );
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public void SynchronousRead_IsRejectedExplicitly()
    {
        var schema = new Schema([], null);
        var result = new DotRocksFlightSqlResult(schema, 0, 0, true, _ => EmptyBatches());
        using var reader = new DotRocksFlightSqlDataReader(result);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => reader.Read());

        Assert.Contains("asynchronous only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_MaterializesListAndStructValues()
    {
        var listType = new ListType(Int32Type.Default);
        var structType = new StructType([
            new Field("code", Int32Type.Default, false),
            new Field("label", StringType.Default, true),
        ]);
        var schema = new Schema(
            [new Field("items", listType, false), new Field("detail", structType, false)],
            null
        );
        using var listValues = new Int32Array.Builder().Append(1).Append(2).Build();
        using var offsets = new ArrowBuffer.Builder<int>().Append(0).Append(2).Build();
        using var list = new ListArray(listType, 1, offsets, listValues, ArrowBuffer.Empty);
        using var codes = new Int32Array.Builder().Append(7).Build();
        using var labels = new StringArray.Builder().Append("seven").Build();
        using var detail = new StructArray(structType, 1, [codes, labels], ArrowBuffer.Empty);
        using var batch = new RecordBatch(schema, [list, detail], 1);
        var result = new DotRocksFlightSqlResult(schema, 1, -1, true, _ => SingleBatch(batch));
        await using var reader = new DotRocksFlightSqlDataReader(result);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        Assert.Equal([1, 2], Assert.IsType<object[]>(reader.GetValue(0)));
        var structValue = Assert.IsType<Dictionary<string, object?>>(reader.GetValue(1));
        Assert.Equal(7, structValue["code"]);
        Assert.Equal("seven", structValue["label"]);
    }

    private static async IAsyncEnumerable<RecordBatch> SingleBatch(RecordBatch batch)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return batch;
    }

    private static async IAsyncEnumerable<RecordBatch> EmptyBatches()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
