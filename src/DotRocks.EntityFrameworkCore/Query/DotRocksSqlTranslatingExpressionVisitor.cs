using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace DotRocks.EntityFrameworkCore.Query;

/// <summary>
/// Adds the StarRocks translations for the relational GREATEST/LEAST hooks, which power the
/// EF-standard <c>EF.Functions.Greatest</c>/<c>Least</c> params-array overloads,
/// <c>Math.Max</c>/<c>Math.Min</c>, and inline-collection <c>Min()</c>/<c>Max()</c>. StarRocks
/// follows MySQL NULL semantics — <c>greatest()</c>/<c>least()</c> return NULL when any argument
/// is NULL — so every argument propagates nullability, unlike SQL Server/PostgreSQL where NULL
/// arguments are ignored.
/// </summary>
internal sealed class DotRocksSqlTranslatingExpressionVisitor(
    RelationalSqlTranslatingExpressionVisitorDependencies dependencies,
    QueryCompilationContext queryCompilationContext,
    QueryableMethodTranslatingExpressionVisitor queryableMethodTranslatingExpressionVisitor
)
    : RelationalSqlTranslatingExpressionVisitor(
        dependencies,
        queryCompilationContext,
        queryableMethodTranslatingExpressionVisitor
    )
{
    public override SqlExpression? GenerateGreatest(
        IReadOnlyList<SqlExpression> expressions,
        Type resultType
    ) => GenerateFunction("greatest", expressions, resultType);

    public override SqlExpression? GenerateLeast(
        IReadOnlyList<SqlExpression> expressions,
        Type resultType
    ) => GenerateFunction("least", expressions, resultType);

    private SqlExpression GenerateFunction(
        string name,
        IReadOnlyList<SqlExpression> expressions,
        Type resultType
    )
    {
        RelationalTypeMapping? typeMapping = ExpressionExtensions.InferTypeMapping([
            .. expressions,
        ]);

        return Dependencies.SqlExpressionFactory.Function(
            name,
            expressions,
            nullable: true,
            argumentsPropagateNullability: Enumerable.Repeat(true, expressions.Count),
            resultType,
            typeMapping
        );
    }
}
