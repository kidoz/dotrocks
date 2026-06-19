using System.Globalization;
using System.Numerics;
using System.Text;

namespace DotRocks.Data;

/// <summary>
/// Represents a lossless StarRocks decimal value as an unscaled integer and base-10 scale.
/// </summary>
public readonly struct DotRocksDecimal
    : IEquatable<DotRocksDecimal>,
        IComparable<DotRocksDecimal>,
        IComparable
{
    private static readonly BigInteger DecimalMaxUnscaled = BigInteger.Parse(
        "79228162514264337593543950335",
        CultureInfo.InvariantCulture
    );

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksDecimal"/> struct.
    /// </summary>
    /// <param name="unscaledValue">The integer value before applying <paramref name="scale"/>.</param>
    /// <param name="scale">The number of fractional decimal digits.</param>
    public DotRocksDecimal(BigInteger unscaledValue, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        UnscaledValue = unscaledValue;
        Scale = scale;
    }

    /// <summary>
    /// Gets the integer value before applying <see cref="Scale"/>.
    /// </summary>
    public BigInteger UnscaledValue { get; }

    /// <summary>
    /// Gets the number of fractional decimal digits.
    /// </summary>
    public int Scale { get; }

    /// <summary>
    /// Converts a <see cref="decimal"/> value to a lossless DotRocks decimal representation.
    /// </summary>
    /// <param name="value">The CLR decimal value.</param>
    /// <returns>The lossless DotRocks decimal representation.</returns>
    public static DotRocksDecimal FromDecimal(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        int scale = (bits[3] >> 16) & 0xFF;
        bool isNegative = (bits[3] & unchecked((int)0x80000000)) != 0;
        BigInteger unscaled =
            (uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);

        return new DotRocksDecimal(isNegative ? -unscaled : unscaled, scale);
    }

    /// <summary>
    /// Parses an invariant-culture decimal string into a lossless DotRocks decimal representation.
    /// </summary>
    /// <param name="value">The decimal text to parse.</param>
    /// <returns>The parsed DotRocks decimal value.</returns>
    /// <exception cref="FormatException">The value is not a valid invariant decimal literal.</exception>
    public static DotRocksDecimal Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ReadOnlySpan<char> span = value.AsSpan().Trim();
        if (span.IsEmpty)
        {
            throw new FormatException("Decimal value is empty.");
        }

        bool isNegative = false;
        if (span[0] is '+' or '-')
        {
            isNegative = span[0] == '-';
            span = span[1..];
        }

        if (span.IsEmpty)
        {
            throw new FormatException("Decimal value does not contain digits.");
        }

        var digits = new StringBuilder(span.Length);
        bool hasDecimalPoint = false;
        bool hasDigits = false;
        int scale = 0;

        foreach (char c in span)
        {
            if (c == '.')
            {
                if (hasDecimalPoint)
                {
                    throw new FormatException(
                        "Decimal value contains more than one decimal point."
                    );
                }

                hasDecimalPoint = true;
                continue;
            }

            if (!char.IsAsciiDigit(c))
            {
                throw new FormatException("Decimal value contains an invalid character.");
            }

            hasDigits = true;
            digits.Append(c);
            if (hasDecimalPoint)
            {
                scale++;
            }
        }

        if (!hasDigits)
        {
            throw new FormatException("Decimal value does not contain digits.");
        }

        BigInteger unscaled = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        if (isNegative && !unscaled.IsZero)
        {
            unscaled = -unscaled;
        }

        return new DotRocksDecimal(unscaled, scale);
    }

    /// <summary>
    /// Converts this value to <see cref="decimal"/> without losing precision.
    /// </summary>
    /// <returns>The exact CLR decimal value.</returns>
    /// <exception cref="DotRocksPrecisionLossException">
    /// The value cannot be represented exactly as <see cref="decimal"/>.
    /// </exception>
    public decimal ToDecimal()
    {
        BigInteger unscaled = UnscaledValue;
        int scale = Scale;
        while (scale > 0 && (scale > 28 || BigInteger.Abs(unscaled) > DecimalMaxUnscaled))
        {
            BigInteger quotient = BigInteger.DivRem(unscaled, 10, out BigInteger remainder);
            if (!remainder.IsZero)
            {
                throw new DotRocksPrecisionLossException(
                    "Decimal value cannot be represented exactly as System.Decimal."
                );
            }

            unscaled = quotient;
            scale--;
        }

        BigInteger magnitude = BigInteger.Abs(unscaled);
        if (scale > 28 || magnitude > DecimalMaxUnscaled)
        {
            throw new DotRocksPrecisionLossException(
                "Decimal value cannot be represented exactly as System.Decimal."
            );
        }

        return new decimal(
            unchecked((int)ExtractUInt32(magnitude, 0)),
            unchecked((int)ExtractUInt32(magnitude, 32)),
            unchecked((int)ExtractUInt32(magnitude, 64)),
            unscaled.Sign < 0,
            (byte)scale
        );
    }

    /// <summary>
    /// Compares this value with another DotRocks decimal value.
    /// </summary>
    /// <param name="other">The value to compare with this instance.</param>
    /// <returns>A signed integer that indicates the relative order of the values.</returns>
    public int CompareTo(DotRocksDecimal other)
    {
        int scale = Math.Max(Scale, other.Scale);
        BigInteger left = UnscaledValue * BigInteger.Pow(10, scale - Scale);
        BigInteger right = other.UnscaledValue * BigInteger.Pow(10, scale - other.Scale);
        return left.CompareTo(right);
    }

    /// <summary>
    /// Compares this value with another object.
    /// </summary>
    /// <param name="obj">The object to compare with this instance.</param>
    /// <returns>A signed integer that indicates the relative order of the values.</returns>
    /// <exception cref="ArgumentException">The object is not a DotRocks decimal value.</exception>
    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        if (obj is DotRocksDecimal other)
        {
            return CompareTo(other);
        }

        throw new ArgumentException("Object must be a DotRocks decimal value.", nameof(obj));
    }

    /// <summary>
    /// Returns a value indicating whether this instance and another DotRocks decimal value are
    /// numerically equal.
    /// </summary>
    /// <param name="other">The value to compare with this instance.</param>
    /// <returns><see langword="true"/> if the values are numerically equal.</returns>
    public bool Equals(DotRocksDecimal other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DotRocksDecimal other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        (BigInteger unscaled, int scale) = Normalize(UnscaledValue, Scale);
        return HashCode.Combine(unscaled, scale);
    }

    /// <summary>
    /// Formats this value as an invariant-culture decimal string.
    /// </summary>
    /// <returns>The invariant decimal representation.</returns>
    public override string ToString()
    {
        BigInteger magnitude = BigInteger.Abs(UnscaledValue);
        string digits = magnitude.ToString(CultureInfo.InvariantCulture);
        string sign = UnscaledValue.Sign < 0 ? "-" : string.Empty;
        if (Scale == 0)
        {
            return sign + digits;
        }

        if (digits.Length <= Scale)
        {
            return sign + "0." + new string('0', Scale - digits.Length) + digits;
        }

        int wholeDigits = digits.Length - Scale;
        return sign + digits[..wholeDigits] + "." + digits[wholeDigits..];
    }

    /// <summary>
    /// Converts a <see cref="decimal"/> value to a lossless DotRocks decimal representation.
    /// </summary>
    /// <param name="value">The CLR decimal value.</param>
    public static implicit operator DotRocksDecimal(decimal value) => FromDecimal(value);

    /// <summary>
    /// Converts a DotRocks decimal value to <see cref="decimal"/> without losing precision.
    /// </summary>
    /// <param name="value">The DotRocks decimal value.</param>
    public static explicit operator decimal(DotRocksDecimal value) => value.ToDecimal();

    /// <summary>
    /// Determines whether two DotRocks decimal values are numerically equal.
    /// </summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> if the values are numerically equal.</returns>
    public static bool operator ==(DotRocksDecimal left, DotRocksDecimal right) =>
        left.Equals(right);

    /// <summary>
    /// Determines whether two DotRocks decimal values are not numerically equal.
    /// </summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> if the values are not numerically equal.</returns>
    public static bool operator !=(DotRocksDecimal left, DotRocksDecimal right) =>
        !left.Equals(right);

    /// <summary>
    /// Determines whether one DotRocks decimal value is less than another.
    /// </summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> if the left value is less than the right value.</returns>
    public static bool operator <(DotRocksDecimal left, DotRocksDecimal right) =>
        left.CompareTo(right) < 0;

    /// <summary>
    /// Determines whether one DotRocks decimal value is less than or equal to another.
    /// </summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns>
    /// <see langword="true"/> if the left value is less than or equal to the right value.
    /// </returns>
    public static bool operator <=(DotRocksDecimal left, DotRocksDecimal right) =>
        left.CompareTo(right) <= 0;

    /// <summary>
    /// Determines whether one DotRocks decimal value is greater than another.
    /// </summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> if the left value is greater than the right value.</returns>
    public static bool operator >(DotRocksDecimal left, DotRocksDecimal right) =>
        left.CompareTo(right) > 0;

    /// <summary>
    /// Determines whether one DotRocks decimal value is greater than or equal to another.
    /// </summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns>
    /// <see langword="true"/> if the left value is greater than or equal to the right value.
    /// </returns>
    public static bool operator >=(DotRocksDecimal left, DotRocksDecimal right) =>
        left.CompareTo(right) >= 0;

    private static uint ExtractUInt32(BigInteger value, int shift) =>
        (uint)((value >> shift) & uint.MaxValue);

    private static (BigInteger UnscaledValue, int Scale) Normalize(
        BigInteger unscaledValue,
        int scale
    )
    {
        if (unscaledValue.IsZero)
        {
            return (BigInteger.Zero, 0);
        }

        while (scale > 0)
        {
            BigInteger quotient = BigInteger.DivRem(unscaledValue, 10, out BigInteger remainder);
            if (!remainder.IsZero)
            {
                break;
            }

            unscaledValue = quotient;
            scale--;
        }

        return (unscaledValue, scale);
    }
}
