using System.Data.Common;

namespace DotRocks.Data;

internal sealed class DotRocksDbColumn : DbColumn
{
    public DotRocksDbColumn(
        string columnName,
        int columnOrdinal,
        Type dataType,
        string dataTypeName,
        bool allowDBNull,
        string? baseCatalogName,
        string? baseSchemaName,
        string? baseTableName,
        string? baseColumnName,
        int columnSize
    )
    {
        ColumnName = columnName;
        ColumnOrdinal = columnOrdinal;
        DataType = dataType;
        DataTypeName = dataTypeName;
        AllowDBNull = allowDBNull;
        BaseCatalogName = baseCatalogName;
        BaseSchemaName = baseSchemaName;
        BaseTableName = baseTableName;
        BaseColumnName = baseColumnName;
        ColumnSize = columnSize;
    }
}
