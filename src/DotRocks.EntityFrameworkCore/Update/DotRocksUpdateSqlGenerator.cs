using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace DotRocks.EntityFrameworkCore.Update;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
    : UpdateSqlGenerator(dependencies)
{
    public override ResultSetMapping AppendInsertOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction
    )
    {
        requiresTransaction = false;
        AppendInsert(commandStringBuilder, command);
        return ResultSetMapping.NoResults;
    }

    public override ResultSetMapping AppendUpdateOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction
    )
    {
        requiresTransaction = false;
        AppendUpdate(commandStringBuilder, command);
        return ResultSetMapping.NoResults;
    }

    public override ResultSetMapping AppendDeleteOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction
    )
    {
        requiresTransaction = false;
        AppendDelete(commandStringBuilder, command);
        return ResultSetMapping.NoResults;
    }

    private void AppendInsert(StringBuilder builder, IReadOnlyModificationCommand command)
    {
        IColumnModification[] writes = command
            .ColumnModifications.Where(operation => operation.IsWrite)
            .ToArray();
        if (writes.Length == 0)
        {
            throw new NotSupportedException(
                "DotRocks EF Core INSERT requires at least one column."
            );
        }

        builder.Append("INSERT INTO ");
        SqlGenerationHelper.DelimitIdentifier(builder, command.TableName, command.Schema);
        builder.Append(" (");
        AppendColumnList(builder, writes);
        builder.Append(") VALUES (");
        AppendParameterList(builder, writes, useOriginalValue: false);
        builder.AppendLine(");");
    }

    private void AppendUpdate(StringBuilder builder, IReadOnlyModificationCommand command)
    {
        IColumnModification[] writes = command
            .ColumnModifications.Where(operation => operation.IsWrite)
            .ToArray();
        IColumnModification[] conditions = command
            .ColumnModifications.Where(operation => operation.IsCondition)
            .ToArray();
        if (writes.Length == 0)
        {
            throw new NotSupportedException(
                "DotRocks EF Core UPDATE requires at least one column."
            );
        }

        if (conditions.Length == 0)
        {
            throw new NotSupportedException(
                "DotRocks EF Core UPDATE requires a primary key condition."
            );
        }

        builder.Append("UPDATE ");
        SqlGenerationHelper.DelimitIdentifier(builder, command.TableName, command.Schema);
        builder.Append(" SET ");
        for (int i = 0; i < writes.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            SqlGenerationHelper.DelimitIdentifier(builder, writes[i].ColumnName);
            builder.Append(" = ");
            AppendParameter(builder, writes[i], useOriginalValue: false);
        }

        AppendWhere(builder, conditions);
        builder.AppendLine(";");
    }

    private void AppendDelete(StringBuilder builder, IReadOnlyModificationCommand command)
    {
        IColumnModification[] conditions = command
            .ColumnModifications.Where(operation => operation.IsCondition)
            .ToArray();
        if (conditions.Length == 0)
        {
            throw new NotSupportedException(
                "DotRocks EF Core DELETE requires a primary key condition."
            );
        }

        builder.Append("DELETE FROM ");
        SqlGenerationHelper.DelimitIdentifier(builder, command.TableName, command.Schema);
        AppendWhere(builder, conditions);
        builder.AppendLine(";");
    }

    private void AppendWhere(StringBuilder builder, IColumnModification[] conditions)
    {
        builder.Append(" WHERE ");
        for (int i = 0; i < conditions.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(" AND ");
            }

            SqlGenerationHelper.DelimitIdentifier(builder, conditions[i].ColumnName);
            builder.Append(" = ");
            AppendParameter(builder, conditions[i], useOriginalValue: true);
        }
    }

    private void AppendColumnList(StringBuilder builder, IColumnModification[] columns)
    {
        for (int i = 0; i < columns.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            SqlGenerationHelper.DelimitIdentifier(builder, columns[i].ColumnName);
        }
    }

    private void AppendParameterList(
        StringBuilder builder,
        IColumnModification[] operations,
        bool useOriginalValue
    )
    {
        for (int i = 0; i < operations.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            AppendParameter(builder, operations[i], useOriginalValue);
        }
    }

    private void AppendParameter(
        StringBuilder builder,
        IColumnModification operation,
        bool useOriginalValue
    )
    {
        string? parameterName = useOriginalValue
            ? operation.OriginalParameterName
            : operation.ParameterName;
        if (string.IsNullOrEmpty(parameterName))
        {
            throw new NotSupportedException(
                "DotRocks EF Core SaveChanges supports only parameterized DML."
            );
        }

        SqlGenerationHelper.GenerateParameterNamePlaceholder(builder, parameterName);
    }
}
