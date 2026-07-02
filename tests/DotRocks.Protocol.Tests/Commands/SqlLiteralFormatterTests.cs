using System.Globalization;
using DotRocks.Data;
using DotRocks.Data.Protocol.Commands;
using Xunit;

namespace DotRocks.Protocol.Tests.Commands;

public sealed class SqlLiteralFormatterTests
{
    [Theory]
    [InlineData(null, "NULL")]
    [InlineData(true, "TRUE")]
    [InlineData(false, "FALSE")]
    [InlineData(123, "123")]
    [InlineData(123.5d, "123.5")]
    [InlineData(123.5f, "123.5")]
    [InlineData("plain text", "'plain text'")] // fast path: no escapable character
    [InlineData("", "''")] // fast path: empty string
    [InlineData("O'Reilly", "'O''Reilly'")]
    [InlineData("back\\slash", "'back\\\\slash'")]
    [InlineData("line\nbreak", "'line\\nbreak'")]
    public void Format_ProducesExpectedSqlLiteral(object? value, string expected)
    {
        Assert.Equal(expected, SqlLiteralFormatter.Format(value));
    }

    [Fact]
    public void Format_EscapesNulAndCtrlZThatBypassTheFastPath()
    {
        // Built at runtime so no raw control character appears in the test source. Both are in
        // the escapable set, so a string containing them must take the escaping slow path.
        Assert.Equal("'nul\\0byte'", SqlLiteralFormatter.Format("nul" + (char)0x00 + "byte"));
        Assert.Equal("'ctrl\\Zz'", SqlLiteralFormatter.Format("ctrl" + (char)0x1A + "z"));
    }

    [Fact]
    public void Format_FormatsDbNullAsNull()
    {
        Assert.Equal("NULL", SqlLiteralFormatter.Format(DBNull.Value));
    }

    [Fact]
    public void Format_UsesInvariantDecimalSeparator()
    {
        Assert.Equal("1234.56", SqlLiteralFormatter.Format(1234.56m));
    }

    [Fact]
    public void Format_FormatsDotRocksDecimalLosslessly()
    {
        DotRocksDecimal value = DotRocksDecimal.Parse(
            "12345678901234567890123456789012345678.9000"
        );

        Assert.Equal(
            "12345678901234567890123456789012345678.9000",
            SqlLiteralFormatter.Format(value)
        );
    }

    [Fact]
    public void Format_FormatsInt128AsLargeIntLiteral()
    {
        Int128 value = Int128.Parse(
            "170141183460469231731687303715884105727",
            CultureInfo.InvariantCulture
        );

        Assert.Equal("170141183460469231731687303715884105727", SqlLiteralFormatter.Format(value));
    }

    [Fact]
    public void Format_FormatsTemporalValues()
    {
        Assert.Equal(
            "'2026-06-19 13:14:15.123456'",
            SqlLiteralFormatter.Format(new DateTime(2026, 6, 19, 13, 14, 15, 123).AddTicks(4560))
        );
        Assert.Equal("'2026-06-19'", SqlLiteralFormatter.Format(new DateOnly(2026, 6, 19)));
        Assert.Equal(
            "'13:14:15.123456'",
            SqlLiteralFormatter.Format(new TimeOnly(13, 14, 15, 123).Add(TimeSpan.FromTicks(4560)))
        );
    }

    [Fact]
    public void Format_FormatsGuidAsStringLiteral()
    {
        var value = Guid.Parse("9f4f591e-3db2-4879-856c-1c54b4241b76");

        Assert.Equal("'9f4f591e-3db2-4879-856c-1c54b4241b76'", SqlLiteralFormatter.Format(value));
    }

    [Fact]
    public void Format_FormatsDotRocksJsonAsStringLiteralPreservingRawText()
    {
        // The exact server representation (spacing, key order) round-trips into the literal.
        var value = new DotRocksJson("{\"b\": 2, \"a\":1}");

        Assert.Equal("'{\"b\": 2, \"a\":1}'", SqlLiteralFormatter.Format(value));
    }

    [Fact]
    public void Format_EscapesDotRocksJsonRawText()
    {
        // Raw JSON text flows through the same escaping as plain strings, so quotes and
        // backslash escapes inside the JSON cannot break out of the SQL literal.
        var value = new DotRocksJson("{\"name\": \"O'Reilly\\n\"}");

        Assert.Equal("'{\"name\": \"O''Reilly\\\\n\"}'", SqlLiteralFormatter.Format(value));
    }

    [Fact]
    public void Format_FormatsBytesAsHexLiteral()
    {
        Assert.Equal("X'00FF10'", SqlLiteralFormatter.Format(new byte[] { 0x00, 0xFF, 0x10 }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Format_RejectsNonFiniteFloatingPointValues(object value)
    {
        Assert.Throws<NotSupportedException>(() => SqlLiteralFormatter.Format(value));
    }

    [Fact]
    public void Format_RejectsUnsupportedValueTypes()
    {
        Assert.Throws<NotSupportedException>(() => SqlLiteralFormatter.Format(new object()));
    }
}
