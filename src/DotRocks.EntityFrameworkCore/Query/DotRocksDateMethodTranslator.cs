using System.Collections.Frozen;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace DotRocks.EntityFrameworkCore.Query;

/// <summary>
/// Translates <see cref="DateTime"/> and <see cref="DateOnly"/> "Add…" methods to the StarRocks
/// plain-argument date-arithmetic functions (<c>days_add</c>, <c>months_add</c>, …), which avoid the
/// <c>INTERVAL</c> syntax. Verified against StarRocks 3.5.21 and 4.1.4.
/// </summary>
internal sealed class DotRocksDateMethodTranslator(ISqlExpressionFactory sqlExpressionFactory)
    : IMethodCallTranslator
{
    private static readonly FrozenDictionary<string, string> DateTimeFunctions = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        [nameof(DateTime.AddYears)] = "years_add",
        [nameof(DateTime.AddMonths)] = "months_add",
        [nameof(DateTime.AddDays)] = "days_add",
        [nameof(DateTime.AddHours)] = "hours_add",
        [nameof(DateTime.AddMinutes)] = "minutes_add",
        [nameof(DateTime.AddSeconds)] = "seconds_add",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> DateOnlyFunctions = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        [nameof(DateOnly.AddYears)] = "years_add",
        [nameof(DateOnly.AddMonths)] = "months_add",
        [nameof(DateOnly.AddDays)] = "days_add",
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
        if (instance is null || arguments.Count != 1)
        {
            return null;
        }

        FrozenDictionary<string, string>? table = method.DeclaringType switch
        {
            { } type when type == typeof(DateTime) => DateTimeFunctions,
            { } type when type == typeof(DateOnly) => DateOnlyFunctions,
            _ => null,
        };

        if (table is null || !table.TryGetValue(method.Name, out string? function))
        {
            return null;
        }

        SqlExpression? count = TranslateCount(arguments[0], method);
        if (count is null)
        {
            return null;
        }

        return sqlExpressionFactory.Function(
            function,
            [instance, count],
            nullable: true,
            argumentsPropagateNullability: [true, true],
            method.ReturnType
        );
    }

    /// <summary>
    /// The StarRocks <c>*_add</c> functions take a whole-number count, while the
    /// <c>AddDays</c>…<c>AddSeconds</c> overloads take a <see cref="double"/>. A fractional count
    /// must never be truncated silently: a constant is refused here, so EF reports the expression
    /// as untranslatable, and a parameter or column is guarded at execution time with
    /// <c>assert_true</c>, which fails the query with a clear message instead.
    /// </summary>
    private SqlExpression? TranslateCount(SqlExpression count, MethodInfo method)
    {
        if (count.Type == typeof(int) || count.Type == typeof(long))
        {
            return count;
        }

        if (count is SqlConstantExpression { Value: double value })
        {
            return value == Math.Truncate(value) && value is >= int.MinValue and <= int.MaxValue
                ? sqlExpressionFactory.Constant((int)value)
                : null;
        }

        // Even an int variable arrives here as a double parameter (EF evaluates the implicit
        // conversion before parameterizing), so refusing every non-constant double would break
        // the common AddDays(days) shape. Guard the value instead.
        SqlExpression wholeNumber = sqlExpressionFactory.OrElse(
            sqlExpressionFactory.IsNull(count),
            sqlExpressionFactory.Equal(
                count,
                sqlExpressionFactory.Function(
                    "floor",
                    [count],
                    nullable: true,
                    argumentsPropagateNullability: [true],
                    typeof(double)
                )
            )
        );
        SqlExpression guard = sqlExpressionFactory.Function(
            "assert_true",
            [
                wholeNumber,
                sqlExpressionFactory.Constant(
                    $"{method.DeclaringType!.Name}.{method.Name} requires a whole-number count; StarRocks date arithmetic takes an integer."
                ),
            ],
            nullable: false,
            argumentsPropagateNullability: [false, false],
            typeof(bool)
        );
        return sqlExpressionFactory.Case(
            [new CaseWhenClause(guard, sqlExpressionFactory.Convert(count, typeof(long)))],
            elseResult: null
        );
    }
}
