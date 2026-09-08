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
    // generator unchanged.
    //
    // Set-modifying LINQ (ExecuteUpdate/ExecuteDelete) needs a StarRocks-specific shape.
    // The StarRocks grammar is `UPDATE <table> SET <column> = <expr> ... WHERE <predicate>`
    // and `DELETE FROM <table> WHERE <predicate>`: the target table cannot carry an alias
    // and SET assignments take bare column names. EF Core's relational generator always
    // aliases the target (`UPDATE `t` AS `t0` ... WHERE `t0`.`id` = ...`), which StarRocks
    // rejects as a syntax error, so the target is emitted without an alias and its columns
    // unqualified. That is only unambiguous while the statement references exactly one
    // relation, so joins and subqueries are refused instead of risking a column silently
    // resolving against the wrong table.
    private string? _dmlTargetAlias;

    protected override Expression VisitDelete(DeleteExpression deleteExpression)
    {
        SelectExpression select = deleteExpression.SelectExpression;
        EnsureSingleTableDml("ExecuteDelete", select, deleteExpression.Table);

        _dmlTargetAlias = deleteExpression.Table.Alias;
        try
        {
            Sql.Append("DELETE FROM ");
            AppendDmlTarget(deleteExpression.Table);
            GenerateDmlPredicate(select.Predicate);
        }
        finally
        {
            _dmlTargetAlias = null;
        }

        return deleteExpression;
    }

    protected override Expression VisitUpdate(UpdateExpression updateExpression)
    {
        SelectExpression select = updateExpression.SelectExpression;
        EnsureSingleTableDml("ExecuteUpdate", select, updateExpression.Table);
        foreach (ColumnValueSetter setter in updateExpression.ColumnValueSetters)
        {
            RejectSubquery("ExecuteUpdate", setter.Value);
        }

        _dmlTargetAlias = updateExpression.Table.Alias;
        try
        {
            Sql.Append("UPDATE ");
            AppendDmlTarget(updateExpression.Table);
            Sql.AppendLine().Append("SET ");
            for (int i = 0; i < updateExpression.ColumnValueSetters.Count; i++)
            {
                if (i > 0)
                {
                    Sql.Append(", ");
                }

                (ColumnExpression column, SqlExpression value) =
                    updateExpression.ColumnValueSetters[i];
                Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(column.Name))
                    .Append(" = ");
                Visit(value);
            }

            GenerateDmlPredicate(select.Predicate);
        }
        finally
        {
            _dmlTargetAlias = null;
        }

        return updateExpression;
    }

    protected override Expression VisitColumn(ColumnExpression columnExpression)
    {
        if (
            _dmlTargetAlias is null
            || !string.Equals(
                columnExpression.TableAlias,
                _dmlTargetAlias,
                StringComparison.Ordinal
            )
        )
        {
            return base.VisitColumn(columnExpression);
        }

        // Inside UPDATE/DELETE the target table has no alias, so its columns are bare names.
        Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(columnExpression.Name));
        return columnExpression;
    }

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

    private void AppendDmlTarget(TableExpression table) =>
        Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(table.Name, table.Schema));

    private void GenerateDmlPredicate(SqlExpression? predicate)
    {
        // StarRocks requires a WHERE clause on UPDATE and DELETE; `WHERE TRUE` is its
        // documented spelling for "every row", which is what an unfiltered
        // ExecuteUpdate/ExecuteDelete means.
        Sql.AppendLine().Append("WHERE ");
        if (predicate is null)
        {
            Sql.Append("TRUE");
            return;
        }

        Visit(predicate);
    }

    private static void EnsureSingleTableDml(
        string operation,
        SelectExpression select,
        TableExpression target
    )
    {
        bool singleTable =
            select.Tables.Count == 1
            && select.Tables[0] is TableExpression table
            && table.Equals(target);
        if (
            !singleTable
            || select.IsDistinct
            || select.Projection.Count > 0
            || select.GroupBy.Count > 0
            || select.Having is not null
            || select.Orderings.Count > 0
            || select.Offset is not null
            || select.Limit is not null
        )
        {
            throw CreateUnsupportedDmlException(operation);
        }

        if (select.Predicate is not null)
        {
            RejectSubquery(operation, select.Predicate);
        }
    }

    private static void RejectSubquery(string operation, SqlExpression expression)
    {
        if (SubqueryDetector.ContainsSubquery(expression))
        {
            throw CreateUnsupportedDmlException(operation);
        }
    }

    private static NotSupportedException CreateUnsupportedDmlException(string operation) =>
        new(
            $"DotRocks EF Core {operation} translates only a single-table query with an optional WHERE predicate over that table's columns; "
                + "joins, subqueries, Distinct, GroupBy, OrderBy, Skip, and Take are not supported because StarRocks UPDATE and DELETE cannot express them safely. "
                + "Use Database.ExecuteSql for those statements."
        );

    /// <summary>
    /// Finds a nested <see cref="SelectExpression"/> anywhere inside a SQL expression tree.
    /// </summary>
    private sealed class SubqueryDetector : ExpressionVisitor
    {
        private bool _found;

        public static bool ContainsSubquery(Expression expression)
        {
            var detector = new SubqueryDetector();
            detector.Visit(expression);
            return detector._found;
        }

        [return: NotNullIfNotNull(nameof(node))]
        public override Expression? Visit(Expression? node)
        {
            if (_found || node is null)
            {
                return node;
            }

            if (node is SelectExpression)
            {
                _found = true;
                return node;
            }

            return base.Visit(node);
        }
    }
}
