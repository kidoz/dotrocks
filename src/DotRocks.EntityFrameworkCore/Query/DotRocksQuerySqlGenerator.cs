using System.Diagnostics.CodeAnalysis;
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
    protected override Expression VisitSelect(SelectExpression selectExpression)
    {
        if (selectExpression.GroupBy.Count > 0)
        {
            throw CreateUnsupportedQueryException("GROUP BY");
        }

        return base.VisitSelect(selectExpression);
    }

    protected override Expression VisitCrossJoin(CrossJoinExpression crossJoinExpression) =>
        throw CreateUnsupportedQueryException("CROSS JOIN");

    protected override Expression VisitInnerJoin(InnerJoinExpression innerJoinExpression) =>
        throw CreateUnsupportedQueryException("JOIN");

    protected override Expression VisitLeftJoin(LeftJoinExpression leftJoinExpression) =>
        throw CreateUnsupportedQueryException("LEFT JOIN");

    protected override Expression VisitRightJoin(RightJoinExpression rightJoinExpression) =>
        throw CreateUnsupportedQueryException("RIGHT JOIN");

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

        if (selectExpression.Offset is not null)
        {
            Sql.AppendLine().Append("OFFSET ");
            Visit(selectExpression.Offset);
        }
    }

    private static NotSupportedException CreateUnsupportedQueryException(string feature) =>
        new($"DotRocks EF Core query translation for {feature} is not implemented yet.");
}
