using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace DotRocks.EntityFrameworkCore.Query;

/// <summary>
/// Translates <see cref="Math"/> methods to the matching StarRocks numeric functions, verified
/// against StarRocks 4.0.7.
/// </summary>
internal sealed class DotRocksMathMethodTranslator(ISqlExpressionFactory sqlExpressionFactory)
    : IMethodCallTranslator
{
    // Single-argument Math methods mapped to a StarRocks function of the same arity.
    private static readonly Dictionary<string, string> SingleArgumentFunctions = new(
        StringComparer.Ordinal
    )
    {
        [nameof(Math.Abs)] = "abs",
        [nameof(Math.Ceiling)] = "ceil",
        [nameof(Math.Floor)] = "floor",
        [nameof(Math.Round)] = "round",
        [nameof(Math.Sqrt)] = "sqrt",
        [nameof(Math.Exp)] = "exp",
        [nameof(Math.Log)] = "ln",
        [nameof(Math.Sign)] = "sign",
    };

    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger
    )
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);
        if (method.DeclaringType != typeof(Math))
        {
            return null;
        }

        // Two-argument forms: Round(value, digits) and Pow(x, y). Only translate Round when the
        // second argument is the digit count; Round(value, MidpointRounding) has no StarRocks
        // equivalent, so fall back to EF's normal translation failure rather than emit wrong SQL.
        if (
            arguments.Count == 2
            && string.Equals(method.Name, nameof(Math.Round), StringComparison.Ordinal)
        )
        {
            return arguments[1].Type == typeof(int) || arguments[1].Type == typeof(long)
                ? Function("round", arguments, method.ReturnType)
                : null;
        }

        if (
            arguments.Count == 2
            && string.Equals(method.Name, nameof(Math.Pow), StringComparison.Ordinal)
        )
        {
            return Function("power", arguments, method.ReturnType);
        }

        if (
            arguments.Count == 1
            && SingleArgumentFunctions.TryGetValue(method.Name, out string? function)
        )
        {
            return Function(function, arguments, method.ReturnType);
        }

        return null;
    }

    private SqlExpression Function(
        string name,
        IReadOnlyList<SqlExpression> arguments,
        Type returnType
    ) =>
        sqlExpressionFactory.Function(
            name,
            arguments,
            nullable: true,
            argumentsPropagateNullability: arguments.Select(_ => true).ToArray(),
            returnType
        );
}
