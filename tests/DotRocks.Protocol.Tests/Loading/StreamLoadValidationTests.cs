using DotRocks.Data.Loading;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class StreamLoadValidationTests
{
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
        const string value = "private-А";
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
