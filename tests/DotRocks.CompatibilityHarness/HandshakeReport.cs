namespace DotRocks.CompatibilityHarness;

/// <summary>
/// A sanitized record of a single observed StarRocks handshake. It deliberately omits the
/// authentication challenge bytes; only their length is recorded.
/// </summary>
internal sealed record HandshakeReport(
    string Host,
    int Port,
    string CapturedAtUtc,
    int ProtocolVersion,
    string ServerVersion,
    uint ConnectionId,
    byte CharacterSet,
    int StatusFlags,
    string? AuthPluginName,
    int AuthPluginDataLength,
    int RawPayloadLength,
    IReadOnlyList<string> Capabilities
);
