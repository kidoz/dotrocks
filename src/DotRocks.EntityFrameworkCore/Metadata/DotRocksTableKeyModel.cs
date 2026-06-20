namespace DotRocks.EntityFrameworkCore.Metadata;

/// <summary>
/// StarRocks table key model used by DotRocks EF Core migrations.
/// </summary>
public enum DotRocksTableKeyModel
{
    /// <summary>
    /// Generates a StarRocks <c>DUPLICATE KEY</c> table.
    /// </summary>
    DuplicateKey,

    /// <summary>
    /// Generates a StarRocks <c>PRIMARY KEY</c> table.
    /// </summary>
    PrimaryKey,

    /// <summary>
    /// Generates a StarRocks <c>UNIQUE KEY</c> table.
    /// </summary>
    UniqueKey,
}
