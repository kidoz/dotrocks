using DotRocks.Data.Protocol.Commands;
using Xunit;

namespace DotRocks.Protocol.Tests.Commands;

public sealed class ReadOnlyQueryClassificationTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData(" /* comment */ select value from t; ")]
    [InlineData("-- comment\nSHOW TABLES")]
    [InlineData("DESCRIBE t")]
    [InlineData("DESC t")]
    public void AllowsSimpleReads(string sql) =>
        Assert.True(SqlStatementClassifier.IsReadOnlyQuery(sql));

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("/* comment */ DELETE FROM t")]
    [InlineData("UPDATE t SET value = 1")]
    [InlineData("SELECT 1; INSERT INTO t VALUES (1)")]
    [InlineData("SELECT 1 INTO OUTFILE 'file'")]
    [InlineData("SELECT @tenant := 1")]
    [InlineData("/*! SELECT 1 */")]
    [InlineData("SELECT 1 /*! INTO OUTFILE 'file' */")]
    [InlineData("WITH cte AS (SELECT 1) SELECT * FROM cte")]
    [InlineData("SELECTED")]
    [InlineData("/* unterminated SELECT 1")]
    [InlineData("")]
    public void RejectsWritesAndAmbiguousForms(string sql) =>
        Assert.False(SqlStatementClassifier.IsReadOnlyQuery(sql));
}
