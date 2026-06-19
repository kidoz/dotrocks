using System.Text;
using DotRocks.Data.Protocol.Commands;
using Xunit;

namespace DotRocks.Protocol.Tests.Commands;

public sealed class QueryCommandBuilderTests
{
    [Fact]
    public void Build_WritesComQueryPayload()
    {
        byte[] payload = QueryCommandBuilder.Build("SELECT 1");

        Assert.Equal(0x03, payload[0]);
        Assert.Equal("SELECT 1", Encoding.UTF8.GetString(payload.AsSpan(1)));
    }

    [Fact]
    public void Build_RejectsEmptyCommandText()
    {
        Assert.Throws<ArgumentException>(() => QueryCommandBuilder.Build(" "));
    }
}
