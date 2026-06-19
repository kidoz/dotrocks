using System.Data;
using System.Data.Common;
using DotRocks.Data;
using DotRocks.Data.Protocol.Commands;
using Xunit;

namespace DotRocks.Protocol.Tests.Commands;

public sealed class CommandTextParameterBinderTests
{
    [Fact]
    public void Bind_ReplacesNamedParameters()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @id, @name" };
        Add(command, "@id", 42);
        Add(command, "name", "O'Reilly");

        string sql = CommandTextParameterBinder.Bind(command.CommandText, command.Parameters);

        Assert.Equal("SELECT 42, 'O''Reilly'", sql);
    }

    [Fact]
    public void Bind_ReplacesRepeatedParameters()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @value + @value" };
        Add(command, "value", 5);

        string sql = CommandTextParameterBinder.Bind(command.CommandText, command.Parameters);

        Assert.Equal("SELECT 5 + 5", sql);
    }

    [Fact]
    public void Bind_DoesNotReplaceInsideStringsCommentsOrQuotedIdentifiers()
    {
        using var command = new DotRocksCommand
        {
            CommandText = "SELECT '@value', `@value`, @value -- @value\n/* @value */",
        };
        Add(command, "value", 7);

        string sql = CommandTextParameterBinder.Bind(command.CommandText, command.Parameters);

        Assert.Equal("SELECT '@value', `@value`, 7 -- @value\n/* @value */", sql);
    }

    [Fact]
    public void Bind_RejectsMissingParameters()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @missing" };
        Add(command, "other", 1);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CommandTextParameterBinder.Bind(command.CommandText, command.Parameters)
        );

        Assert.Contains("@missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_RejectsUnusedParameters()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT 1" };
        Add(command, "unused", 1);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CommandTextParameterBinder.Bind(command.CommandText, command.Parameters)
        );

        Assert.Contains("@unused", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_RejectsDuplicateParameterNames()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @id" };
        Add(command, "@id", 1);
        Add(command, "id", 2);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CommandTextParameterBinder.Bind(command.CommandText, command.Parameters)
        );

        Assert.Contains("@id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_RejectsUnsupportedDirections()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @id" };
        command.Parameters.Add(
            new DotRocksParameter
            {
                ParameterName = "id",
                Value = 1,
                Direction = ParameterDirection.Output,
            }
        );

        Assert.Throws<NotSupportedException>(() =>
            CommandTextParameterBinder.Bind(command.CommandText, command.Parameters)
        );
    }

    [Fact]
    public void Bind_RejectsUnsupportedPositionalPlaceholders()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT ?" };
        Add(command, "value", 1);

        Assert.Throws<NotSupportedException>(() =>
            CommandTextParameterBinder.Bind(command.CommandText, command.Parameters)
        );
    }

    [Fact]
    public void Bind_RejectsInvalidParameterNames()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @1" };
        Add(command, "1", 1);

        Assert.Throws<ArgumentException>(() =>
            CommandTextParameterBinder.Bind(command.CommandText, command.Parameters)
        );
    }

    [Fact]
    public void Bind_RejectsUnsupportedParameterValues()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @value" };
        Add(command, "value", new object());

        Assert.Throws<NotSupportedException>(() =>
            CommandTextParameterBinder.Bind(command.CommandText, command.Parameters)
        );
    }

    [Fact]
    public void Bind_LeavesServerVariablesUntouched()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @@version, @value" };
        Add(command, "value", 1);

        string sql = CommandTextParameterBinder.Bind(command.CommandText, command.Parameters);

        Assert.Equal("SELECT @@version, 1", sql);
    }

    [Fact]
    public void Prepare_ValidatesNamedParameterShapeWithoutFormattingValues()
    {
        using var command = new DotRocksCommand
        {
            CommandText = "SELECT @id, '@ignored', `@ignored`, @@version -- @ignored",
        };
        Add(command, "id", new object());

        PreparedCommandText prepared = CommandTextParameterBinder.Prepare(
            command.CommandText,
            command.Parameters
        );

        Assert.Equal(command.CommandText, prepared.CommandText);
        Assert.Equal(["id"], prepared.ParameterNames);
        Assert.True(prepared.HasPlaceholders);
    }

    [Fact]
    public void Prepare_RejectsMissingParameters()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @missing" };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CommandTextParameterBinder.Prepare(command.CommandText, command.Parameters)
        );

        Assert.Contains("@missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_RejectsUnusedParameters()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT 1" };
        Add(command, "unused", 1);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CommandTextParameterBinder.Prepare(command.CommandText, command.Parameters)
        );

        Assert.Contains("@unused", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_RejectsUnsupportedDirections()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @id" };
        command.Parameters.Add(
            new DotRocksParameter
            {
                ParameterName = "id",
                Value = 1,
                Direction = ParameterDirection.ReturnValue,
            }
        );

        Assert.Throws<NotSupportedException>(() =>
            CommandTextParameterBinder.Prepare(command.CommandText, command.Parameters)
        );
    }

    [Fact]
    public void Prepare_RejectsPositionalPlaceholders()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT ?" };

        Assert.Throws<NotSupportedException>(() =>
            CommandTextParameterBinder.Prepare(command.CommandText, command.Parameters)
        );
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        command.Parameters.Add(new DotRocksParameter { ParameterName = name, Value = value });
    }
}
