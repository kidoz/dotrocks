namespace DotRocks.Data.Protocol.Handshake;

/// <summary>
/// Client/server capability flags exchanged during the StarRocks (MySQL-compatible) handshake.
/// DotRocks advertises only the subset it actually implements and that StarRocks accepts; this
/// enum names the flags DotRocks reasons about, not the entire MySQL set.
/// </summary>
[Flags]
internal enum CapabilityFlags : uint
{
    None = 0,
    LongPassword = 0x0000_0001,
    FoundRows = 0x0000_0002,
    LongFlag = 0x0000_0004,
    ConnectWithDb = 0x0000_0008,
    NoSchema = 0x0000_0010,
    Compress = 0x0000_0020,
    Odbc = 0x0000_0040,
    LocalFiles = 0x0000_0080,
    IgnoreSpace = 0x0000_0100,
    Protocol41 = 0x0000_0200,
    Interactive = 0x0000_0400,
    Ssl = 0x0000_0800,
    IgnoreSigpipe = 0x0000_1000,
    Transactions = 0x0000_2000,
    SecureConnection = 0x0000_8000,
    MultiStatements = 0x0001_0000,
    MultiResults = 0x0002_0000,
    PreparedStatementMultiResults = 0x0004_0000,
    PluginAuth = 0x0008_0000,
    ConnectAttributes = 0x0010_0000,
    PluginAuthLengthEncodedClientData = 0x0020_0000,
    CanHandleExpiredPasswords = 0x0040_0000,
    SessionTrack = 0x0080_0000,
    DeprecateEof = 0x0100_0000,
    OptionalResultSetMetadata = 0x0200_0000,
}
