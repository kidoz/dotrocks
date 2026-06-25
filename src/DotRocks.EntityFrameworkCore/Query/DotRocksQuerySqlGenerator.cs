using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace DotRocks.EntityFrameworkCore.Query;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core query pipeline constructs this internal type through the provider factory."
)]
internal sealed class DotRocksQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
    : QuerySqlGenerator(dependencies)
{
    // StarRocks is an MPP analytics engine; INNER/LEFT/RIGHT/CROSS JOIN and
    // GROUP BY/HAVING are first-class SQL and are emitted by the base relational
    // generator unchanged. Only set-modifying LINQ (ExecuteUpdate/ExecuteDelete)
    // remains unsupported, because StarRocks does not accept the UPDATE/DELETE
    // shapes EF Core produces for arbitrary key models.
    protected override Expression VisitDelete(DeleteExpression deleteExpression) =>
        throw CreateUnsupportedQueryException("LINQ DELETE");

    protected override Expression VisitUpdate(UpdateExpression updateExpression) =>
        throw CreateUnsupportedQueryException("LINQ UPDATE");

    protected override void GenerateLimitOffset(SelectExpression selectExpression)
    {
        if (selectExpression.Limit is not null)
        {
            Sql.AppendLine().Append("LIMIT ");
            Visit(selectExpression.Limit);
        }
        else if (selectExpression.Offset is not null)
        {
            // StarRocks requires LIMIT to precede OFFSET; a bare OFFSET is a syntax error.
            // Synthesize an effectively-unbounded LIMIT for Skip-without-Take queries.
            Sql.AppendLine()
                .Append("LIMIT ")
                .Append(long.MaxValue.ToString(CultureInfo.InvariantCulture));
        }

        if (selectExpression.Offset is not null)
        {
            Sql.AppendLine().Append("OFFSET ");
            Visit(selectExpression.Offset);
        }
    }

    private static NotSupportedException CreateUnsupportedQueryException(string feature) =>
        new($"DotRocks EF Core query translation for {feature} is not implemented yet.");
}
