using System.Collections.Frozen;
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
    private static readonly FrozenDictionary<string, string> SingleArgumentFunctions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(Math.Abs)] = "abs",
            [nameof(Math.Ceiling)] = "ceil",
            [nameof(Math.Floor)] = "floor",
            [nameof(Math.Sqrt)] = "sqrt",
            [nameof(Math.Exp)] = "exp",
            [nameof(Math.Log)] = "ln",
            [nameof(Math.Sign)] = "sign",
        }.ToFrozenDictionary(StringComparer.Ordinal);

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

        // Math.Round defaults to midpoint-to-even. StarRocks ROUND rounds midpoints away from
        // zero and also accepts negative digit counts that .NET rejects. Do not claim equivalent
        // CLR semantics for any Round overload until it has a verified exact translation.

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
            argumentsPropagateNullability: Enumerable.Repeat(true, arguments.Count),
            returnType
        );
}
