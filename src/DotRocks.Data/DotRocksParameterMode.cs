namespace DotRocks.Data;

/// <summary>
/// Selects how a <see cref="DotRocksCommand"/> binds parameters and executes against StarRocks.
/// </summary>
/// <remarks>
/// <see cref="Auto"/> uses the verified client-side text protocol. <see cref="ServerPrepared"/>
/// uses StarRocks server-side prepared statements and is limited to the server behavior DotRocks
/// has characterized.
/// </remarks>
public enum DotRocksParameterMode
{
    /// <summary>
    /// Chooses a verified mechanism automatically. Currently equivalent to
    /// <see cref="TextProtocol"/>.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Binds parameters with the safe client-side tokenizer and serializer and executes over the
    /// text protocol.
    /// </summary>
    TextProtocol = 1,

    /// <summary>
    /// Uses the StarRocks server-side prepared-statement binary protocol. Unsupported statements
    /// or value types fail with a <see cref="DotRocksUnsupportedFeatureException"/> or a
    /// StarRocks server error rather than falling back to another mechanism.
    /// </summary>
    ServerPrepared = 2,
}
