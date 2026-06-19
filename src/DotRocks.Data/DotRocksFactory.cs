using System.Data.Common;

namespace DotRocks.Data;

/// <summary>
/// Provides factory methods for DotRocks ADO.NET provider objects.
/// </summary>
public sealed class DotRocksFactory : DbProviderFactory
{
    /// <summary>
    /// Gets the shared DotRocks provider factory instance.
    /// </summary>
    public static DotRocksFactory Instance { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksFactory"/> class.
    /// </summary>
    public DotRocksFactory() { }

    /// <inheritdoc />
    public override DbCommand CreateCommand() => new DotRocksCommand();

    /// <inheritdoc />
    public override DbConnection CreateConnection() => new DotRocksConnection();

    /// <inheritdoc />
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() =>
        new DotRocksConnectionStringBuilder();

    /// <inheritdoc />
    public override DbDataSource CreateDataSource(string connectionString) =>
        new DotRocksDataSource(connectionString);

    /// <inheritdoc />
    public override DbParameter CreateParameter() => new DotRocksParameter();
}
