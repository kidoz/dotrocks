using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace DotRocks.EntityFrameworkCore.Query;

/// <summary>
/// Translates <see cref="DateTime"/> and <see cref="DateOnly"/> component members (Year, Month,
/// Day, …) to the matching StarRocks date functions, verified against StarRocks 4.0.7.
/// </summary>
internal sealed class DotRocksDateTimeMemberTranslator(ISqlExpressionFactory sqlExpressionFactory)
    : IMemberTranslator
{
    public SqlExpression? Translate(
        SqlExpression? instance,
        MemberInfo member,
        Type returnType,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger
    )
    {
        if (
            instance is null
            || (
                member.DeclaringType != typeof(DateTime) && member.DeclaringType != typeof(DateOnly)
            )
        )
        {
            return null;
        }

        string? function = member.Name switch
        {
            nameof(DateTime.Year) => "year",
            nameof(DateTime.Month) => "month",
            nameof(DateTime.Day) => "day",
            nameof(DateTime.Hour) when member.DeclaringType == typeof(DateTime) => "hour",
            nameof(DateTime.Minute) when member.DeclaringType == typeof(DateTime) => "minute",
            nameof(DateTime.Second) when member.DeclaringType == typeof(DateTime) => "second",
            nameof(DateTime.DayOfYear) => "dayofyear",
            _ => null,
        };

        if (function is not null)
        {
            return Function(function, instance, returnType);
        }

        if (
            member.DeclaringType == typeof(DateTime)
            && string.Equals(member.Name, nameof(DateTime.Date), StringComparison.Ordinal)
        )
        {
            return Function("date", instance, returnType);
        }

        return null;
    }

    private SqlExpression Function(string name, SqlExpression argument, Type returnType) =>
        sqlExpressionFactory.Function(
            name,
            [argument],
            nullable: true,
            argumentsPropagateNullability: [true],
            returnType
        );
}
