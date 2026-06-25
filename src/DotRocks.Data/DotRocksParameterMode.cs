namespace DotRocks.Data;

/// <summary>
/// Selects how a <see cref="DotRocksCommand"/> binds parameters and executes against StarRocks.
/// </summary>
/// <remarks>
/// Server-side prepared statements are not yet verified against StarRocks (only client-side
/// validation is implemented), so <see cref="Auto"/> always uses the verified text protocol and
/// <see cref="ServerPrepared"/> fails explicitly with a
/// <see cref="DotRocksUnsupportedFeatureException"/>.
/// </remarks>
public enum DotRocksParameterMode
{
    /// <summary>
    /// Chooses a verified mechanism automatically. Currently equivalent to
    /// <see cref="TextProtocol"/> because the server-side prepared protocol is unverified.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Binds parameters with the safe client-side tokenizer and serializer and executes over the
    /// text protocol.
    /// </summary>
    TextProtocol = 1,

    /// <summary>
    /// Requests the StarRocks server-side prepared-statement (binary) protocol. Not yet supported;
    /// selecting this mode fails with a <see cref="DotRocksUnsupportedFeatureException"/> rather
    /// than silently using a different mechanism.
    /// </summary>
    ServerPrepared = 2,
}
