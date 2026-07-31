using Apache.Arrow;
using DotRocks.FlightSql;
using Xunit;

namespace DotRocks.FlightSql.Tests;

public sealed class DotRocksFlightSqlResultTests
{
    [Fact]
    public void Constructor_NormalizesUnknownTotals()
    {
        var result = new DotRocksFlightSqlResult(null, -1, -1, false, _ => EmptyBatches());

        Assert.Null(result.TotalRecords);
        Assert.Null(result.TotalBytes);
        Assert.False(result.IsOrdered);
    }

    [Fact]
    public void ReadRecordBatchesAsync_IsSingleUse()
    {
        var result = new DotRocksFlightSqlResult(null, 0, 0, true, _ => EmptyBatches());

        _ = result.ReadRecordBatchesAsync(TestContext.Current.CancellationToken);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            result.ReadRecordBatchesAsync(TestContext.Current.CancellationToken)
        );
        Assert.Contains("only once", exception.Message, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<RecordBatch> EmptyBatches()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
