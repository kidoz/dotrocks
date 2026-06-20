namespace DotRocks.Data;

/// <summary>
/// Controls TLS use for the StarRocks SQL protocol connection.
/// </summary>
public enum DotRocksSslMode
{
    /// <summary>
    /// Do not request TLS for the SQL protocol connection.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Require TLS for the SQL protocol connection and fail when the server cannot negotiate it.
    /// </summary>
    Required = 1,
}
