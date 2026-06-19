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
internal sealed class DotRocksParameterBasedSqlProcessorFactory(
    RelationalParameterBasedSqlProcessorDependencies dependencies
) : IRelationalParameterBasedSqlProcessorFactory
{
    public RelationalParameterBasedSqlProcessor Create(
        RelationalParameterBasedSqlProcessorParameters parameters
    ) => new DotRocksParameterBasedSqlProcessor(dependencies, parameters);
}

internal sealed class DotRocksParameterBasedSqlProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters
) : RelationalParameterBasedSqlProcessor(dependencies, parameters)
{
    protected override Expression ProcessSqlNullability(
        Expression queryExpression,
        ParametersCacheDecorator Decorator
    ) =>
        new DotRocksSqlNullabilityProcessor(Dependencies, Parameters).Process(
            queryExpression,
            Decorator
        );
}

internal sealed class DotRocksSqlNullabilityProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters
) : SqlNullabilityProcessor(dependencies, parameters)
{
    protected override SqlExpression VisitSqlBinary(
        SqlBinaryExpression sqlBinaryExpression,
        bool allowOptimizedExpansion,
        out bool nullable
    )
    {
        if (
            sqlBinaryExpression.OperatorType is ExpressionType.Equal or ExpressionType.NotEqual
            && TryCreateNullParameterComparison(sqlBinaryExpression, allowOptimizedExpansion)
                is { } comparison
        )
        {
            nullable = false;
            return comparison;
        }

        return base.VisitSqlBinary(sqlBinaryExpression, allowOptimizedExpansion, out nullable);
    }

    private SqlExpression? TryCreateNullParameterComparison(
        SqlBinaryExpression sqlBinaryExpression,
        bool allowOptimizedExpansion
    )
    {
        if (
            TryGetNullParameter(sqlBinaryExpression.Left, out SqlParameterExpression? leftParameter)
            && IsNullConstant(sqlBinaryExpression.Right)
        )
        {
            return CreateParameterNullCheck(leftParameter, sqlBinaryExpression.OperatorType);
        }

        if (
            TryGetNullParameter(
                sqlBinaryExpression.Right,
                out SqlParameterExpression? rightParameter
            ) && IsNullConstant(sqlBinaryExpression.Left)
        )
        {
            return CreateParameterNullCheck(rightParameter, sqlBinaryExpression.OperatorType);
        }

        if (TryGetNullParameter(sqlBinaryExpression.Left, out leftParameter))
        {
            SqlExpression right = Visit(sqlBinaryExpression.Right, allowOptimizedExpansion, out _);
            return CreateNullParameterComparison(
                right,
                leftParameter,
                sqlBinaryExpression.OperatorType
            );
        }

        if (TryGetNullParameter(sqlBinaryExpression.Right, out rightParameter))
        {
            SqlExpression left = Visit(sqlBinaryExpression.Left, allowOptimizedExpansion, out _);
            return CreateNullParameterComparison(
                left,
                rightParameter,
                sqlBinaryExpression.OperatorType
            );
        }

        return null;
    }

    private SqlExpression CreateNullParameterComparison(
        SqlExpression expression,
        SqlParameterExpression parameter,
        ExpressionType operatorType
    )
    {
        ISqlExpressionFactory sqlExpressionFactory = Dependencies.SqlExpressionFactory;
        SqlExpression expressionNullCheck =
            operatorType == ExpressionType.Equal
                ? sqlExpressionFactory.IsNull(expression)
                : sqlExpressionFactory.IsNotNull(expression);
        return sqlExpressionFactory.AndAlso(
            expressionNullCheck,
            sqlExpressionFactory.IsNull(parameter)
        );
    }

    private SqlExpression CreateParameterNullCheck(
        SqlParameterExpression parameter,
        ExpressionType operatorType
    )
    {
        ISqlExpressionFactory sqlExpressionFactory = Dependencies.SqlExpressionFactory;
        return operatorType == ExpressionType.Equal
            ? sqlExpressionFactory.IsNull(parameter)
            : sqlExpressionFactory.IsNotNull(parameter);
    }

    private bool TryGetNullParameter(
        SqlExpression expression,
        [NotNullWhen(true)] out SqlParameterExpression? parameter
    )
    {
        parameter = expression as SqlParameterExpression;
        return parameter is not null && ParametersDecorator.IsNull(parameter.Name);
    }

    private static bool IsNullConstant(SqlExpression expression) =>
        expression is SqlConstantExpression { Value: null };
}
