using DotRocks.Data.Loading;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class DotRocksStreamLoadResultTests
{
    [Theory]
    [InlineData("""{"Status":"Success","Status":"Fail"}""")]
    [InlineData("""{"Status":"Success","status":"Fail"}""")]
    [InlineData("""{"Status":"Success","TxnId":1,"txnid":2}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"Status":"Success","NumberLoadedRows":1.5}""")]
    [InlineData("""{"Status":"Success","NumberLoadedRows":9223372036854775808}""")]
    [InlineData("""{"Status":"Success","NumberLoadedRows":"invalid"}""")]
    [InlineData("""{"Status":"Success","Seq":2147483648}""")]
    [InlineData("""{"Status":true}""")]
    public void Parse_RejectsMalformedResponses(string json)
    {
        Assert.Throws<DotRocksStreamLoadException>(() => DotRocksStreamLoadResult.Parse(json));
    }

    [Fact]
    public void Parse_DoesNotExposeUntrustedPropertyNamesInException()
    {
        const string json = """{"private-row-secret":1,"private-row-secret":2}""";

        DotRocksStreamLoadException exception = Assert.Throws<DotRocksStreamLoadException>(() =>
            DotRocksStreamLoadResult.Parse(json)
        );

        Assert.DoesNotContain("private-row-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_PreservesCaseInsensitiveNamesAndStringEncodedNumbers()
    {
        DotRocksStreamLoadResult result = DotRocksStreamLoadResult.Parse(
            """{"status":"Success","numberloadedrows":"12","txnid":"42","seq":"3","FutureField":true}"""
        );

        Assert.True(result.IsSuccess);
        Assert.Equal(12, result.NumberLoadedRows);
        Assert.Equal(42, result.TransactionId);
        Assert.Equal(3, result.Sequence);
        Assert.Equal(0, result.NumberFilteredRows);
        Assert.Null(result.Message);
    }
}
