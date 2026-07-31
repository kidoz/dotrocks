using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace DotRocks.EntityFrameworkCore.Query;

/// <summary>
/// Translates <see cref="DotRocksDbFunctionsExtensions"/> methods to the matching native
/// StarRocks functions, verified against StarRocks 4.0.7. StarRocks follows MySQL NULL
/// semantics: <c>greatest()</c>/<c>least()</c> return NULL when any argument is NULL, so all
/// arguments propagate nullability.
/// </summary>
internal sealed class DotRocksDbFunctionsMethodTranslator(
    ISqlExpressionFactory sqlExpressionFactory
) : IMethodCallTranslator
{
    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger
    )
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);
        if (method.DeclaringType != typeof(DotRocksDbFunctionsExtensions))
        {
            return null;
        }

        string? function = method.Name switch
        {
            nameof(DotRocksDbFunctionsExtensions.Greatest) => "greatest",
            nameof(DotRocksDbFunctionsExtensions.Least) => "least",
            _ => null,
        };
        if (function is null)
        {
            return null;
        }

        // The first argument is the EF.Functions instance; the compared values follow it.
        SqlExpression[] values = [.. arguments.Skip(1)];
        RelationalTypeMapping? typeMapping = ExpressionExtensions.InferTypeMapping(values);
        SqlExpression[] typedValues =
        [
            .. values.Select(value => sqlExpressionFactory.ApplyTypeMapping(value, typeMapping)),
        ];

        return sqlExpressionFactory.Function(
            function,
            typedValues,
            nullable: true,
            argumentsPropagateNullability: Enumerable.Repeat(true, typedValues.Length),
            method.ReturnType,
            typeMapping
        );
    }
}
