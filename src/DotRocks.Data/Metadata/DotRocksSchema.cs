using System.Data;
using System.Data.Common;
using System.Globalization;

namespace DotRocks.Data.Metadata;

/// <summary>
/// Builds the ADO.NET schema metadata collections returned by
/// <see cref="DotRocksConnection.GetSchema()"/> from StarRocks <c>INFORMATION_SCHEMA</c>. Each
/// collection is a query with fixed column aliases loaded into a <see cref="DataTable"/>; restriction
/// values are passed as parameters, never concatenated.
/// </summary>
internal static class DotRocksSchema
{
    public const string MetaDataCollectionsName = "MetaDataCollections";

    private static readonly string[] CollectionNames =
    [
        MetaDataCollectionsName,
        "Databases",
        "Tables",
        "Views",
        "Columns",
    ];

    public static DataTable GetSchema(
        DotRocksConnection connection,
        string? collectionName,
        string?[]? restrictions
    )
    {
        collectionName ??= MetaDataCollectionsName;
        return collectionName.ToUpperInvariant() switch
        {
            "METADATACOLLECTIONS" => BuildMetaDataCollections(),
            "DATABASES" or "SCHEMAS" => Query(
                connection,
                "SELECT CATALOG_NAME AS catalog_name, SCHEMA_NAME AS database_name, "
                    + "DEFAULT_CHARACTER_SET_NAME AS default_character_set, "
                    + "DEFAULT_COLLATION_NAME AS default_collation FROM information_schema.schemata",
                ["SCHEMA_NAME"],
                restrictions
            ),
            "TABLES" => Query(
                connection,
                "SELECT TABLE_CATALOG AS table_catalog, TABLE_SCHEMA AS table_schema, "
                    + "TABLE_NAME AS table_name, TABLE_TYPE AS table_type FROM information_schema.tables "
                    + "WHERE TABLE_TYPE = 'BASE TABLE'",
                ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME"],
                restrictions
            ),
            "VIEWS" => Query(
                connection,
                "SELECT TABLE_CATALOG AS table_catalog, TABLE_SCHEMA AS table_schema, "
                    + "TABLE_NAME AS table_name, TABLE_TYPE AS table_type FROM information_schema.tables "
                    + "WHERE TABLE_TYPE IN ('VIEW', 'SYSTEM VIEW')",
                ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME"],
                restrictions
            ),
            "COLUMNS" => Query(
                connection,
                "SELECT TABLE_CATALOG AS table_catalog, TABLE_SCHEMA AS table_schema, "
                    + "TABLE_NAME AS table_name, COLUMN_NAME AS column_name, "
                    + "ORDINAL_POSITION AS ordinal_position, COLUMN_DEFAULT AS column_default, "
                    + "IS_NULLABLE AS is_nullable, DATA_TYPE AS data_type, "
                    + "COLUMN_TYPE AS column_type, COLUMN_KEY AS column_key FROM information_schema.columns",
                ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME"],
                restrictions
            ),
            _ => throw new ArgumentException(
                $"DotRocks does not support the metadata collection '{collectionName}'.",
                nameof(collectionName)
            ),
        };
    }

    private static DataTable BuildMetaDataCollections()
    {
        var table = new DataTable(MetaDataCollectionsName)
        {
            Locale = CultureInfo.InvariantCulture,
        };
        table.Columns.Add("CollectionName", typeof(string));
        table.Columns.Add("NumberOfRestrictions", typeof(int));
        table.Columns.Add("NumberOfIdentifierParts", typeof(int));

        table.Rows.Add(MetaDataCollectionsName, 0, 0);
        table.Rows.Add("Databases", 1, 1);
        table.Rows.Add("Tables", 3, 3);
        table.Rows.Add("Views", 3, 3);
        table.Rows.Add("Columns", 4, 4);
        return table;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The SQL is composed from fixed literals plus '@' parameter placeholders; restriction values are parameters."
    )]
    private static DataTable Query(
        DotRocksConnection connection,
        string baseQuery,
        string[] restrictionColumns,
        string?[]? restrictions
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        bool hasWhere = baseQuery.Contains("WHERE", StringComparison.Ordinal);

        using DbCommand command = connection.CreateCommand();
        var builder = new System.Text.StringBuilder(baseQuery);
        int restrictionCount = Math.Min(restrictionColumns.Length, restrictions?.Length ?? 0);
        for (int i = 0; i < restrictionCount; i++)
        {
            if (restrictions![i] is not { } value)
            {
                continue;
            }

            builder.Append(hasWhere ? " AND " : " WHERE ");
            hasWhere = true;
            string parameterName = "@r" + i.ToString(CultureInfo.InvariantCulture);
            builder.Append(restrictionColumns[i]).Append(" = ").Append(parameterName);
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        command.CommandText = builder.ToString();

        var table = new DataTable("SchemaResult") { Locale = CultureInfo.InvariantCulture };
        using DbDataReader reader = command.ExecuteReader();
        table.Load(reader);
        return table;
    }

    public static IReadOnlyList<string> SupportedCollections => CollectionNames;
}
