using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data;
using DotRocks.Data.Protocol.Commands;
using Xunit;

namespace DotRocks.Protocol.Tests.Commands;

/// <summary>
/// Fuzz coverage for parameter tokenization and literal serialization. Hostile or malformed command
/// text (unbalanced quotes, comments, dangling placeholders) and arbitrary parameter values must
/// either bind safely or fail with a controlled exception, never an uncontrolled crash such as
/// <see cref="IndexOutOfRangeException"/> or <see cref="NullReferenceException"/>. The tokenizer must
/// also never treat a placeholder inside a string, identifier, or comment as a parameter.
/// </summary>
public sealed class ParameterBinderFuzzTests
{
    // Characters that exercise the tokenizer's quote, identifier, comment, and placeholder paths.
    private static readonly char[] InterestingChars = "ab12 @:?'\"`\\-/*\n(),;=.".ToCharArray();

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int seed = 0; seed < 64; seed++)
        {
            data.Add(seed);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    [SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification = "A seeded, reproducible PRNG is required for deterministic fuzzing; it is not security-sensitive."
    )]
    public void Bind_DoesNotCrashOnRandomCommandText(int seed)
    {
        var random = new Random(seed);
        for (int iteration = 0; iteration < 64; iteration++)
        {
            int length = random.Next(0, 48);
            char[] text = new char[length];
            for (int i = 0; i < length; i++)
            {
                text[i] = InterestingChars[random.Next(InterestingChars.Length)];
            }

            AssertControlledBind(new string(text));
        }
    }

    [Theory]
    [InlineData("SELECT '")]
    [InlineData("SELECT \"")]
    [InlineData("SELECT `")]
    [InlineData("SELECT /*")]
    [InlineData("SELECT @")]
    [InlineData("SELECT @@value")]
    [InlineData("SELECT @value -- @value")]
    [InlineData("SELECT '@value")]
    [InlineData("SELECT `@value")]
    [InlineData("SELECT @value /* unterminated")]
    [InlineData("@a@b@value")]
    [InlineData("''''''")]
    [InlineData("\\\\\\")]
    public void Bind_HandlesAdversarialCommandText(string commandText)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        AssertControlledBind(commandText);
    }

    [Fact]
    public void Format_DoesNotCrashOnDiverseValues()
    {
        object?[] values =
        [
            null!,
            DBNull.Value,
            true,
            (sbyte)-1,
            (short)-2,
            -3,
            -4L,
            (Int128)(-5),
            1.5f,
            double.NaN,
            double.PositiveInfinity,
            123.456m,
            "plain",
            "O'Reilly",
            "back\\slash",
            "new\nline",
            "embedded\0zero",
            "`quoted`",
            new byte[] { 0x00, 0x01, 0xFF },
            new DateTime(2026, 6, 25, 1, 2, 3, DateTimeKind.Unspecified),
            new DateOnly(2026, 6, 25),
            new TimeOnly(1, 2, 3),
            Guid.Empty,
        ];

        foreach (object? value in values)
        {
            AssertControlledFormat(value);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The fuzz harness intentionally inspects every exception to fail on any uncontrolled crash."
    )]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Fuzz input is assigned to CommandText to test the binder's safety; nothing is executed against a server."
    )]
    private static void AssertControlledBind(string commandText)
    {
        using var command = new DotRocksCommand { CommandText = commandText };
        AddParameter(command, "@a", 1);
        AddParameter(command, "@b", "x");
        AddParameter(command, "@value", "v");

        try
        {
            string sql = CommandTextParameterBinder.Bind(command.CommandText, command.Parameters);

            // A bound string-literal or comment must not have had its inner @value substituted.
            if (commandText.Contains("'@value'", StringComparison.Ordinal))
            {
                Assert.Contains("'@value'", sql, StringComparison.Ordinal);
            }
        }
        catch (Exception exception) when (IsControlled(exception))
        {
            // Expected: a documented validation failure (missing/unused parameter, bad syntax).
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"Uncontrolled {exception.GetType().Name} binding [{commandText}]: {exception.Message}"
            );
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The fuzz harness intentionally inspects every exception to fail on any uncontrolled crash."
    )]
    private static void AssertControlledFormat(object? value)
    {
        try
        {
            _ = SqlLiteralFormatter.Format(value);
        }
        catch (Exception exception) when (IsControlled(exception))
        {
            // Expected: an unsupported value type fails with a controlled exception.
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"Uncontrolled {exception.GetType().Name} formatting '{value}': {exception.Message}"
            );
        }
    }

    private static bool IsControlled(Exception exception) =>
        exception
            is ArgumentException
                or InvalidOperationException
                or FormatException
                or NotSupportedException
                or DotRocksException;

    private static void AddParameter(DotRocksCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
