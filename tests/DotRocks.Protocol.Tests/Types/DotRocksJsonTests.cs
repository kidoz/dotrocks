using System.Text.Json;
using DotRocks.Data;
using Xunit;

namespace DotRocks.Protocol.Tests.Types;

public sealed class DotRocksJsonTests
{
    [Fact]
    public void Constructor_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => new DotRocksJson(null!));

    [Fact]
    public void RawText_AndToString_PreserveExactInput()
    {
        // Spacing and key order are preserved exactly (StarRocks returns "key": value with a space).
        const string raw = """{"b": 2, "a": [1, 2], "s": "x"}""";
        var json = new DotRocksJson(raw);

        Assert.Equal(raw, json.RawText);
        Assert.Equal(raw, json.ToString());
    }

    [Fact]
    public void Parse_ReturnsIndependentDocument()
    {
        var json = new DotRocksJson("""{"a": 1, "b": [2, 3]}""");

        using JsonDocument document = json.Parse();

        Assert.Equal(1, document.RootElement.GetProperty("a").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("b")[0].GetInt32());
    }

    [Fact]
    public void Parse_OnInvalidJson_Throws()
    {
        var json = new DotRocksJson("not json");

        Assert.ThrowsAny<JsonException>(() => json.Parse());
    }

    [Fact]
    public void Equality_IsOrdinalOnRawText()
    {
        Assert.Equal(new DotRocksJson("""{"a": 1}"""), new DotRocksJson("""{"a": 1}"""));
        // Semantically equal but differently formatted values are not equal.
        Assert.NotEqual(new DotRocksJson("""{"a": 1}"""), new DotRocksJson("""{"a":1}"""));
        Assert.Equal(
            new DotRocksJson("""{"a": 1}""").GetHashCode(),
            new DotRocksJson("""{"a": 1}""").GetHashCode()
        );
    }
}
