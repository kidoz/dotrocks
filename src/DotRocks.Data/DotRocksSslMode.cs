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
    /// Use TLS when the StarRocks server advertises support for it and continue without encryption
    /// when it does not. This is the default: it is secure against a passive eavesdropper on a
    /// TLS-capable server while remaining compatible with plaintext-only deployments. It does not
    /// defend against an active attacker who strips the server's advertised TLS capability to force
    /// a downgrade; use <see cref="Required"/> when that threat is in scope.
    /// </summary>
    Preferred = 1,

    /// <summary>
    /// Require TLS for the SQL protocol connection and fail when the server cannot negotiate it.
    /// </summary>
    Required = 2,
}
