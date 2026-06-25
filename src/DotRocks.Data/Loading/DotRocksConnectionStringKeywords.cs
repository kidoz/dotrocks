using System.Collections.Frozen;
using System.Collections.Immutable;

namespace DotRocks.Data.Loading;

/// <summary>
/// The single source of truth for connection-string keyword aliases, shared by
/// <see cref="DotRocksConnectionOptions"/> and <see cref="DotRocksConnectionStringBuilder"/> so the
/// two cannot drift apart. Each canonical keyword maps to the spellings DotRocks accepts for it
/// (the canonical name first), keyed ordinally and frozen for fast, allocation-free lookups.
/// </summary>
internal static class DotRocksConnectionStringKeywords
{
    private static readonly FrozenDictionary<string, ImmutableArray<string>> AliasesByCanonical =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["Server"] = ["Server", "Host", "Data Source"],
            ["User ID"] = ["User ID", "UserID", "User", "Uid", "Username"],
            ["Password"] = ["Password", "Pwd"],
            ["Database"] = ["Database", "Initial Catalog"],
            ["Connection Timeout"] = ["Connection Timeout", "Connect Timeout", "Timeout"],
            ["Minimum Pool Size"] = ["Minimum Pool Size", "Min Pool Size"],
            ["Maximum Pool Size"] = ["Maximum Pool Size", "Max Pool Size"],
            ["Connection Idle Timeout"] = ["Connection Idle Timeout", "Idle Timeout"],
            ["Ssl Mode"] = ["Ssl Mode", "SSL Mode", "SslMode"],
            ["Trust Server Certificate"] = ["Trust Server Certificate", "TrustServerCertificate"],
            ["Ssl Revocation Check"] =
            [
                "Ssl Revocation Check",
                "SSL Revocation Check",
                "SslRevocationCheck",
                "Revocation",
            ],
            ["Allow Insecure Stream Load"] =
            [
                "Allow Insecure Stream Load",
                "AllowInsecureStreamLoad",
                "Allow Insecure StreamLoad",
            ],
            ["Stream Load Endpoint"] =
            [
                "Stream Load Endpoint",
                "StreamLoadEndpoint",
                "Stream Load URL",
                "Http Endpoint",
            ],
            ["Connection Retries"] =
            [
                "Connection Retries",
                "ConnectionRetries",
                "Connect Retry Count",
            ],
            ["Connection Retry Delay"] =
            [
                "Connection Retry Delay",
                "ConnectionRetryDelay",
                "Retry Delay",
            ],
            ["Connection Lifetime"] = ["Connection Lifetime", "ConnectionLifetime", "Lifetime"],
            ["Server Compatibility Level"] =
            [
                "Server Compatibility Level",
                "ServerCompatibilityLevel",
                "Compatibility Level",
            ],
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Returns the accepted spellings for a canonical keyword, or just the keyword itself when it
    /// has no registered aliases.
    /// </summary>
    public static ImmutableArray<string> Aliases(string canonical) =>
        AliasesByCanonical.TryGetValue(canonical, out ImmutableArray<string> aliases)
            ? aliases
            : [canonical];
}
