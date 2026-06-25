namespace DotRocks.EntityFrameworkCore.Metadata;

/// <summary>
/// Recognizes StarRocks table key-model annotation values for the runtime EF Core services. The
/// single source of truth for the accepted spellings, so the model validator and the migrations SQL
/// generator cannot drift apart on which values are valid or how they map to DDL.
/// </summary>
internal static class DotRocksTableKeyModels
{
    /// <summary>
    /// Recognizes a <see cref="DotRocksAnnotationNames.KeyModel"/> value, which may be a
    /// <see cref="DotRocksTableKeyModel"/> or its StarRocks SQL spelling
    /// (<c>DUPLICATE KEY</c>/<c>PRIMARY KEY</c>/<c>UNIQUE KEY</c>). Returns <see langword="false"/>
    /// for anything else.
    /// </summary>
    public static bool TryParse(object? value, out DotRocksTableKeyModel keyModel)
    {
        switch (value)
        {
            case DotRocksTableKeyModel model:
                keyModel = model;
                return true;
            case string text when string.Equals(
                text,
                "DUPLICATE KEY",
                StringComparison.OrdinalIgnoreCase
            ):
                keyModel = DotRocksTableKeyModel.DuplicateKey;
                return true;
            case string text when string.Equals(
                text,
                "PRIMARY KEY",
                StringComparison.OrdinalIgnoreCase
            ):
                keyModel = DotRocksTableKeyModel.PrimaryKey;
                return true;
            case string text when string.Equals(
                text,
                "UNIQUE KEY",
                StringComparison.OrdinalIgnoreCase
            ):
                keyModel = DotRocksTableKeyModel.UniqueKey;
                return true;
            default:
                keyModel = default;
                return false;
        }
    }

    /// <summary>Returns the StarRocks SQL key clause for a recognized key model.</summary>
    public static string ToKeyClause(DotRocksTableKeyModel keyModel) =>
        keyModel switch
        {
            DotRocksTableKeyModel.DuplicateKey => "DUPLICATE KEY",
            DotRocksTableKeyModel.PrimaryKey => "PRIMARY KEY",
            DotRocksTableKeyModel.UniqueKey => "UNIQUE KEY",
            _ => throw new ArgumentOutOfRangeException(
                nameof(keyModel),
                keyModel,
                "Unknown StarRocks table key model."
            ),
        };
}
