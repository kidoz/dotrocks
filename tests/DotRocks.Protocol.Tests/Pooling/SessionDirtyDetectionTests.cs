using DotRocks.Data.Protocol.Commands;
using Xunit;

namespace DotRocks.Protocol.Tests.Pooling;

public sealed class SessionDirtyDetectionTests
{
    [Theory]
    [InlineData("USE analytics")]
    [InlineData("use analytics")]
    [InlineData("  USE analytics")]
    [InlineData("\n\tUSE analytics")]
    [InlineData("SET SESSION query_timeout = 30")]
    [InlineData("set names utf8")]
    [InlineData("SET @marker = 1")]
    [InlineData("-- pick a database\nUSE analytics")]
    [InlineData("# comment\nSET names utf8")]
    [InlineData("/* block */ USE analytics")]
    [InlineData("/* multi\nline */SET names utf8")]
    // User-variable assignment via ":=" mutates session state without a leading SET/USE keyword;
    // it must be flagged so the connection is retired instead of leaking the variable to the next
    // lease of a pooled connection.
    [InlineData("SELECT @tenant := 7")]
    [InlineData("SELECT @tenant := ?")]
    [InlineData("select @x:=@x+1 from t")]
    public void IsSessionMutatingStatement_FlagsUseAndSet(string commandText) =>
        Assert.True(SqlStatementClassifier.IsSessionMutating(commandText));

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("INSERT INTO t (id) VALUES (1)")]
    [InlineData("UPDATE t SET value = 1 WHERE id = 1")]
    [InlineData("SELECT * FROM users WHERE name = 'SET'")]
    [InlineData("SELECT * FROM settings")]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- USE analytics")]
    public void IsSessionMutatingStatement_AllowsNonSessionStatements(string commandText) =>
        Assert.False(SqlStatementClassifier.IsSessionMutating(commandText));
}
