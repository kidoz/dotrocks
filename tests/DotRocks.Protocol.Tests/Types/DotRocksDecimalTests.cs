using System.Globalization;
using System.Numerics;
using DotRocks.Data;
using Xunit;

namespace DotRocks.Protocol.Tests.Types;

public sealed class DotRocksDecimalTests
{
    [Theory]
    [InlineData("0", "0", 0)]
    [InlineData(
        "12345678901234567890123456789012345678",
        "12345678901234567890123456789012345678",
        0
    )]
    [InlineData("12.3400", "12.3400", 4)]
    [InlineData("-12.3400", "-12.3400", 4)]
    [InlineData(
        "0.00000000000000000000000000000000000001",
        "0.00000000000000000000000000000000000001",
        38
    )]
    public void Parse_PreservesScaleAndDigits(string text, string expectedText, int expectedScale)
    {
        DotRocksDecimal value = DotRocksDecimal.Parse(text);

        Assert.Equal(expectedText, value.ToString());
        Assert.Equal(expectedScale, value.Scale);
    }

    [Fact]
    public void Constructor_RejectsNegativeScale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DotRocksDecimal(BigInteger.One, -1));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("1.2.3")]
    [InlineData("1,234")]
    [InlineData("1e2")]
    public void Parse_InvalidText_ThrowsFormatException(string text)
    {
        Assert.Throws<FormatException>(() => DotRocksDecimal.Parse(text));
    }

    [Fact]
    public void FromDecimal_PreservesClrDecimalScale()
    {
        DotRocksDecimal value = DotRocksDecimal.FromDecimal(12.3400m);

        Assert.Equal(new BigInteger(123400), value.UnscaledValue);
        Assert.Equal(4, value.Scale);
        Assert.Equal("12.3400", value.ToString());
    }

    [Theory]
    [InlineData("12.34", "12.34")]
    [InlineData("-12.34", "-12.34")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    [InlineData("79228162514264337593543950335.0", "79228162514264337593543950335")]
    [InlineData("1.23000000000000000000000000000", "1.23")]
    public void ToDecimal_ReturnsExactDecimal(string text, string expected)
    {
        decimal value = DotRocksDecimal.Parse(text).ToDecimal();

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("79228162514264337593543950336")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("12345678901234567890123456789012345678.9000")]
    public void ToDecimal_WhenValueCannotFitExactly_ThrowsPrecisionLoss(string text)
    {
        DotRocksDecimal value = DotRocksDecimal.Parse(text);

        Assert.Throws<DotRocksPrecisionLossException>(() => value.ToDecimal());
    }

    [Fact]
    public void EqualityAndHashing_AreNumeric()
    {
        DotRocksDecimal left = DotRocksDecimal.Parse("1.0");
        DotRocksDecimal right = DotRocksDecimal.Parse("1.00");

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Comparison_OrdersNumericValues()
    {
        Assert.True(DotRocksDecimal.Parse("-1.20") < DotRocksDecimal.Parse("-1.19"));
        Assert.True(DotRocksDecimal.Parse("1.20") <= DotRocksDecimal.Parse("1.200"));
        Assert.True(DotRocksDecimal.Parse("1.21") > DotRocksDecimal.Parse("1.20"));
    }

    [Fact]
    public void ChangeType_ToDecimal_ConvertsExactly()
    {
        // Convert.ChangeType is what generic row mappers apply to GetValue results, so the
        // struct must participate through IConvertible instead of throwing InvalidCastException.
        object value = DotRocksDecimal.Parse("-12.340000000000");

        Assert.Equal(
            -12.340000000000m,
            Convert.ChangeType(value, typeof(decimal), CultureInfo.InvariantCulture)
        );
    }

    [Fact]
    public void ChangeType_ToDecimal_WhenValueCannotFitExactly_ThrowsPrecisionLoss()
    {
        object value = DotRocksDecimal.Parse("1234567890123456789012345678901234.5678");

        Assert.Throws<DotRocksPrecisionLossException>(() =>
            Convert.ChangeType(value, typeof(decimal), CultureInfo.InvariantCulture)
        );
    }

    [Fact]
    public void ChangeType_ToDouble_ParsesWideValuesCorrectlyRounded()
    {
        object value = DotRocksDecimal.Parse("1234567890123456789012345678901234.5678");

        Assert.Equal(
            1234567890123456789012345678901234.5678d,
            Convert.ChangeType(value, typeof(double), CultureInfo.InvariantCulture)
        );
    }

    [Theory]
    [InlineData("2.5", 2L)]
    [InlineData("3.5", 4L)]
    [InlineData("-2.5", -2L)]
    [InlineData("-3.5", -4L)]
    [InlineData("2.51", 3L)]
    [InlineData("7.00", 7L)]
    public void ChangeType_ToIntegral_RoundsHalfToEvenLikeSystemDecimal(string text, long expected)
    {
        DotRocksDecimal value = DotRocksDecimal.Parse(text);

        Assert.Equal(
            Convert.ToInt64(decimal.Parse(text, CultureInfo.InvariantCulture)),
            Convert.ChangeType(value, typeof(long), CultureInfo.InvariantCulture)
        );
        Assert.Equal(
            expected,
            Convert.ChangeType(value, typeof(long), CultureInfo.InvariantCulture)
        );
    }

    [Fact]
    public void ChangeType_ToIntegral_WhenValueExceedsTargetRange_ThrowsOverflow()
    {
        // A value wider than the target must overflow, not lose precision silently.
        object value = DotRocksDecimal.Parse("12345678901234567890123456789012345678.9");

        Assert.Throws<OverflowException>(() =>
            Convert.ChangeType(value, typeof(long), CultureInfo.InvariantCulture)
        );
    }

    [Fact]
    public void ChangeType_ToStringAndBoolean_UseCanonicalSemantics()
    {
        object value = DotRocksDecimal.Parse("12.34");
        object zero = DotRocksDecimal.Parse("0.00");

        Assert.Equal(
            "12.34",
            Convert.ChangeType(value, typeof(string), CultureInfo.InvariantCulture)
        );
        Assert.Equal(true, Convert.ChangeType(value, typeof(bool), CultureInfo.InvariantCulture));
        Assert.Equal(false, Convert.ChangeType(zero, typeof(bool), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ChangeType_ToUnsupportedTypes_ThrowsInvalidCast()
    {
        object value = DotRocksDecimal.Parse("12.34");

        Assert.Throws<InvalidCastException>(() =>
            Convert.ChangeType(value, typeof(DateTime), CultureInfo.InvariantCulture)
        );
        Assert.Throws<InvalidCastException>(() =>
            Convert.ChangeType(value, typeof(char), CultureInfo.InvariantCulture)
        );
        Assert.Throws<InvalidCastException>(() =>
            Convert.ChangeType(value, typeof(Guid), CultureInfo.InvariantCulture)
        );
    }

    [Fact]
    public void ChangeType_ToEnum_ThrowsInvalidCastLikeSystemDecimal()
    {
        // Type.GetTypeCode reports an enum as its underlying integral type; the conversion must
        // be refused rather than returning a boxed integer for an enum target.
        object value = DotRocksDecimal.Parse("3");

        Assert.Throws<InvalidCastException>(() =>
            Convert.ChangeType(value, typeof(DayOfWeek), CultureInfo.InvariantCulture)
        );
        Assert.Throws<InvalidCastException>(() =>
            Convert.ChangeType(3m, typeof(DayOfWeek), CultureInfo.InvariantCulture)
        );
    }

    [Fact]
    public void ChangeType_ToDotRocksDecimal_ReturnsSameValue()
    {
        object value = DotRocksDecimal.Parse("12.34");

        Assert.Equal(
            DotRocksDecimal.Parse("12.34"),
            Convert.ChangeType(value, typeof(DotRocksDecimal), CultureInfo.InvariantCulture)
        );
    }
}
