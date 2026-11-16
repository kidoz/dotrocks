using DotRocks.Data.Loading;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class StreamLoadValidationTests
{
    [Theory]
    [InlineData(0.1, "1")]
    [InlineData(1.5, "2")]
    [InlineData(2.0, "2")]
    public void Timeouts_RoundUpToServerSeconds(double seconds, string expected)
    {
        var load = new DotRocksStreamLoadOptions { Timeout = TimeSpan.FromSeconds(seconds) };
        Assert.Equal(expected, load.BuildHeaders(DotRocksStreamLoadFormat.Csv)["timeout"]);
        var transaction = new DotRocksStreamLoadTransactionOptions
        {
            Label = "test",
            Timeout = load.Timeout,
            IdleTimeout = load.Timeout,
            PreparedTimeout = load.Timeout,
        };
        Assert.Equal(expected, transaction.BuildBeginHeaders("db", "table")["timeout"]);
        Assert.Equal(
            expected,
            transaction.BuildBeginHeaders("db", "table")["idle_transaction_timeout"]
        );
        Assert.Equal(expected, transaction.BuildPrepareHeaders("db")["prepared_timeout"]);
    }

    [Theory]
    [InlineData("Label")]
    [InlineData("Columns")]
    [InlineData("Where")]
    [InlineData("ColumnSeparator")]
    [InlineData("RowDelimiter")]
    [InlineData("JsonPaths")]
    [InlineData("Partitions")]
    public void NonAsciiHeaders_FailWithOptionName(string option)
    {
        const string value = "private-\u0410";
        var options = new DotRocksStreamLoadOptions();
        switch (option)
        {
            case "Label":
                options.Label = value;
                break;
            case "Columns":
                options.Columns = value;
                break;
            case "Where":
                options.Where = value;
                break;
            case "ColumnSeparator":
                options.ColumnSeparator = value;
                break;
            case "RowDelimiter":
                options.RowDelimiter = value;
                break;
            case "JsonPaths":
                options.JsonPaths = value;
                break;
            case "Partitions":
                options.Partitions = [value];
                break;
        }
        var exception = Assert.Throws<ArgumentException>(() =>
            options.BuildHeaders(DotRocksStreamLoadFormat.Json)
        );
        Assert.Equal(option, exception.ParamName);
        Assert.DoesNotContain(value, exception.ToString(), StringComparison.Ordinal);
    }
}
