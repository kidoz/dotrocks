using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace DotRocks.EntityFrameworkCore.Query;

internal sealed class DotRocksStringMethodTranslator(ISqlExpressionFactory sqlExpressionFactory)
    : IMethodCallTranslator
{
    private static readonly MethodInfo StartsWithMethod =
        typeof(string).GetRuntimeMethod(nameof(string.StartsWith), [typeof(string)])
        ?? throw new InvalidOperationException("Could not find string.StartsWith(string).");

    private static readonly MethodInfo EndsWithMethod =
        typeof(string).GetRuntimeMethod(nameof(string.EndsWith), [typeof(string)])
        ?? throw new InvalidOperationException("Could not find string.EndsWith(string).");

    private static readonly MethodInfo ContainsMethod =
        typeof(string).GetRuntimeMethod(nameof(string.Contains), [typeof(string)])
        ?? throw new InvalidOperationException("Could not find string.Contains(string).");

    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger
    )
    {
        if (instance is null || arguments.Count != 1 || arguments[0].Type != typeof(string))
        {
            return null;
        }

        SqlExpression escapedArgument = EscapeLikePattern(arguments[0]);
        SqlExpression pattern;
        if (method == StartsWithMethod)
        {
            pattern = Concat(escapedArgument, Constant("%"));
        }
        else if (method == EndsWithMethod)
        {
            pattern = Concat(Constant("%"), escapedArgument);
        }
        else if (method == ContainsMethod)
        {
            pattern = Concat(Constant("%"), escapedArgument, Constant("%"));
        }
        else
        {
            return null;
        }

        return sqlExpressionFactory.Like(instance, pattern, Constant("\\"));
    }

    private SqlExpression Constant(string value) => sqlExpressionFactory.Constant(value);

    private SqlExpression EscapeLikePattern(SqlExpression value)
    {
        if (value is SqlConstantExpression { Value: string text })
        {
            return Constant(EscapeLikePattern(text));
        }

        return Replace(Replace(Replace(value, "\\", "\\\\"), "%", "\\%"), "_", "\\_");
    }

    private SqlExpression Replace(SqlExpression value, string search, string replacement) =>
        sqlExpressionFactory.Function(
            "REPLACE",
            [value, Constant(search), Constant(replacement)],
            nullable: true,
            argumentsPropagateNullability: [true, false, false],
            typeof(string)
        );

    private SqlExpression Concat(params SqlExpression[] arguments) =>
        sqlExpressionFactory.Function(
            "CONCAT",
            arguments,
            nullable: true,
            argumentsPropagateNullability: arguments.Select(_ => true),
            typeof(string)
        );

    private static string EscapeLikePattern(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
