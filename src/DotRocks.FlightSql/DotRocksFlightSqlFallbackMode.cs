namespace DotRocks.FlightSql;

/// <summary>
/// Selects explicit MySQL-protocol fallback behavior for the Flight SQL ADO.NET connection.
/// </summary>
[Flags]
public enum DotRocksFlightSqlFallbackMode
{
    /// <summary>No fallback is allowed.</summary>
    None = 0,

    /// <summary>
    /// Retry query discovery over <c>DotRocks.Data</c> when Flight is unavailable or unimplemented.
    /// </summary>
    ReadQueries = 1,

    /// <summary>
    /// Route write commands through <c>DotRocks.Data</c> without first attempting Flight.
    /// </summary>
    WriteCommands = 2,
}
