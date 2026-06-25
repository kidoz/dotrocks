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
/// <c>INTERVAL</c> syntax. Verified against StarRocks 4.0.7.
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

        // StarRocks date-add functions take a whole-number count; the AddX(double) overloads with a
        // fractional argument are not representable, so EF falls back to its normal failure.
        SqlExpression count = arguments[0];
        if (count.Type != typeof(int) && count.Type != typeof(long))
        {
            count = sqlExpressionFactory.Convert(count, typeof(long));
        }

        return sqlExpressionFactory.Function(
            function,
            [instance, count],
            nullable: true,
            argumentsPropagateNullability: [true, true],
            method.ReturnType
        );
    }
}
