using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;

namespace DotRocks.Data;

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

    /// <summary>
    /// Looks up a keyword value trying each accepted spelling in registration order (the canonical
    /// name first).
    /// </summary>
    public static bool TryGetValue(
        DbConnectionStringBuilder builder,
        string canonical,
        out object? value
    )
    {
        foreach (string keyword in Aliases(canonical))
        {
            if (builder.TryGetValue(keyword, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    public static string GetString(
        DbConnectionStringBuilder builder,
        string canonical,
        string fallback
    ) =>
        TryGetValue(builder, canonical, out object? value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : fallback;

    public static int GetInt32(DbConnectionStringBuilder builder, string canonical, int fallback) =>
        TryGetValue(builder, canonical, out object? value)
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : fallback;

    public static bool GetBoolean(
        DbConnectionStringBuilder builder,
        string canonical,
        bool fallback
    ) =>
        TryGetValue(builder, canonical, out object? value)
            ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            : fallback;

    public static TEnum GetEnum<TEnum>(
        DbConnectionStringBuilder builder,
        string canonical,
        TEnum fallback
    )
        where TEnum : struct, Enum
    {
        if (!TryGetValue(builder, canonical, out object? value))
        {
            return fallback;
        }

        if (value is TEnum typed)
        {
            // A boxed enum can already be undefined — e.g. builder.SslMode = (DotRocksSslMode)99
            // through the typed setter stores a boxed value that never went through the string
            // parse below. Validate it here too so the typed path cannot bypass the defined-member
            // check and silently fall back to the least-secure switch arm (plaintext).
            if (!Enum.IsDefined(typed))
            {
                throw new ArgumentException(
                    $"{canonical} value '{typed}' is not supported.",
                    nameof(builder)
                );
            }

            return typed;
        }

        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        // Enum.TryParse accepts any numeric string (e.g. "Ssl Mode=3") and returns an undefined
        // enum value, which for a security-relevant setting like Ssl Mode would silently fall
        // through to the least-secure switch arm (plaintext). Require the parsed value to name a
        // defined member so unrecognized configuration fails closed instead of failing open.
        if (Enum.TryParse(text, ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"{canonical} value '{text}' is not supported.",
            nameof(builder)
        );
    }

    public static Uri GetUri(DbConnectionStringBuilder builder, string canonical, Uri fallback) =>
        TryGetValue(builder, canonical, out object? value)
            ? new Uri(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                UriKind.Absolute
            )
            : fallback;
}
