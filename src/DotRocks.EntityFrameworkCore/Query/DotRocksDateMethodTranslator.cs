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

        if (arguments[0] is SqlConstantExpression { Value: double constant })
        {
            return TranslateConstantDoubleCount(instance, constant, method, function);
        }

        SqlExpression? count = TranslateCount(arguments[0], method);
        if (count is null)
        {
            return null;
        }

        return CreateDateAdd(function, instance, count, method.ReturnType);
    }

    private SqlExpression? TranslateConstantDoubleCount(
        SqlExpression instance,
        double count,
        MethodInfo method,
        string function
    )
    {
        if (!TryConvertWholeNumber(count, out long wholeCount))
        {
            return null;
        }

        if (wholeCount is >= int.MinValue and <= int.MaxValue)
        {
            return CreateDateAdd(
                function,
                instance,
                sqlExpressionFactory.Constant((int)wholeCount),
                method.ReturnType
            );
        }

        int unitsPerDay = method.Name switch
        {
            nameof(DateTime.AddMinutes) => 1_440,
            nameof(DateTime.AddSeconds) => 86_400,
            _ => 0,
        };
        if (unitsPerDay == 0)
        {
            return null;
        }

        long days = wholeCount / unitsPerDay;
        long remainder = wholeCount % unitsPerDay;
        if (days is < int.MinValue or > int.MaxValue)
        {
            return null;
        }

        SqlExpression date = CreateDateAdd(
            "days_add",
            instance,
            sqlExpressionFactory.Constant((int)days),
            method.ReturnType
        );
        return remainder == 0
            ? date
            : CreateDateAdd(
                function,
                date,
                sqlExpressionFactory.Constant((int)remainder),
                method.ReturnType
            );
    }

    private SqlExpression CreateDateAdd(
        string function,
        SqlExpression instance,
        SqlExpression count,
        Type returnType
    )
    {
        return sqlExpressionFactory.Function(
            function,
            [instance, count],
            nullable: true,
            argumentsPropagateNullability: [true, true],
            returnType
        );
    }

    /// <summary>
    /// The StarRocks <c>*_add</c> functions take a whole-number count, while the
    /// <c>AddDays</c>…<c>AddSeconds</c> overloads take a <see cref="double"/>. A fractional count
    /// must never be truncated silently. A parameter or column is guarded at execution time with
    /// <c>assert_true</c>, which also enforces the StarRocks <c>INT</c> count range and fails the
    /// query with a clear message instead.
    /// </summary>
    private SqlExpression? TranslateCount(SqlExpression count, MethodInfo method)
    {
        if (count.Type == typeof(int) || count.Type == typeof(long))
        {
            return count;
        }

        // Even an int variable arrives here as a double parameter (EF evaluates the implicit
        // conversion before parameterizing), so refusing every non-constant double would break
        // the common AddDays(days) shape. Guard both the value and StarRocks' INT range instead.
        SqlExpression validCount = sqlExpressionFactory.OrElse(
            sqlExpressionFactory.IsNull(count),
            sqlExpressionFactory.AndAlso(
                sqlExpressionFactory.Equal(
                    count,
                    sqlExpressionFactory.Function(
                        "floor",
                        [count],
                        nullable: true,
                        argumentsPropagateNullability: [true],
                        typeof(double)
                    )
                ),
                sqlExpressionFactory.AndAlso(
                    sqlExpressionFactory.GreaterThanOrEqual(
                        count,
                        sqlExpressionFactory.Constant((double)int.MinValue)
                    ),
                    sqlExpressionFactory.LessThanOrEqual(
                        count,
                        sqlExpressionFactory.Constant((double)int.MaxValue)
                    )
                )
            )
        );
        SqlExpression guard = sqlExpressionFactory.Function(
            "assert_true",
            [
                validCount,
                sqlExpressionFactory.Constant(
                    $"{method.DeclaringType!.Name}.{method.Name} requires a whole-number count within the StarRocks INT range."
                ),
            ],
            nullable: false,
            argumentsPropagateNullability: [false, false],
            typeof(bool)
        );
        return sqlExpressionFactory.Case(
            [new CaseWhenClause(guard, sqlExpressionFactory.Convert(count, typeof(int)))],
            elseResult: null
        );
    }

    private static bool TryConvertWholeNumber(double value, out long result)
    {
        const double Int64UpperBoundExclusive = 9_223_372_036_854_775_808d;
        if (
            value != Math.Truncate(value)
            || value < long.MinValue
            || value >= Int64UpperBoundExclusive
        )
        {
            result = default;
            return false;
        }

        result = (long)value;
        return true;
    }
}
