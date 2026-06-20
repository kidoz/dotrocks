using System.Data.Common;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotRocks.Data;

namespace DotRocks.Data.Loading;

internal sealed record DotRocksConnectionOptions(
    string Server,
    int Port,
    string UserId,
    string Password,
    string Database,
    TimeSpan ConnectionTimeout,
    bool Pooling,
    int MinimumPoolSize,
    int MaximumPoolSize,
    TimeSpan ConnectionIdleTimeout,
    DotRocksSslMode SslMode,
    bool TrustServerCertificate,
    X509RevocationMode SslRevocationMode,
    Uri StreamLoadEndpoint,
    bool AllowInsecureStreamLoad,
    int MaxConnectionRetries,
    TimeSpan ConnectionRetryDelay,
    string ConnectionString
)
{
    public const string DefaultServer = "127.0.0.1";
    public const int DefaultPort = 9030;
    public const string DefaultUserId = "root";
    public const int DefaultConnectionTimeoutSeconds = 15;
    public const bool DefaultPooling = false;
    public const int DefaultMinimumPoolSize = 0;
    public const int DefaultMaximumPoolSize = 100;
    public const int DefaultConnectionIdleTimeoutSeconds = 300;
    public const int DefaultStreamLoadPort = 8030;
    public const X509RevocationMode DefaultSslRevocationMode = X509RevocationMode.Offline;
    public const int DefaultMaxConnectionRetries = 0;
    public const int DefaultConnectionRetryDelayMilliseconds = 200;

    public static DotRocksConnectionOptions Default { get; } =
        new(
            DefaultServer,
            DefaultPort,
            DefaultUserId,
            string.Empty,
            string.Empty,
            TimeSpan.FromSeconds(DefaultConnectionTimeoutSeconds),
            DefaultPooling,
            DefaultMinimumPoolSize,
            DefaultMaximumPoolSize,
            TimeSpan.FromSeconds(DefaultConnectionIdleTimeoutSeconds),
            DotRocksSslMode.Disabled,
            false,
            DefaultSslRevocationMode,
            BuildDefaultStreamLoadEndpoint(DefaultServer),
            false,
            DefaultMaxConnectionRetries,
            TimeSpan.FromMilliseconds(DefaultConnectionRetryDelayMilliseconds),
            string.Empty
        );

    public static DotRocksConnectionOptions Parse(string? connectionString)
    {
        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString ?? string.Empty,
        };

        return Parse(builder);
    }

    public static DotRocksConnectionOptions Parse(DbConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string server = GetString(builder, "Server", DefaultServer);
        int port = GetInt32(builder, "Port", DefaultPort);
        string userId = GetString(builder, "User ID", DefaultUserId);
        string password = GetString(builder, "Password", string.Empty);
        string database = GetString(builder, "Database", string.Empty);
        int timeoutSeconds = GetInt32(
            builder,
            "Connection Timeout",
            DefaultConnectionTimeoutSeconds
        );
        bool pooling = GetBoolean(builder, "Pooling", DefaultPooling);
        int minimumPoolSize = GetInt32(builder, "Minimum Pool Size", DefaultMinimumPoolSize);
        int maximumPoolSize = GetInt32(builder, "Maximum Pool Size", DefaultMaximumPoolSize);
        int idleTimeoutSeconds = GetInt32(
            builder,
            "Connection Idle Timeout",
            DefaultConnectionIdleTimeoutSeconds
        );
        DotRocksSslMode sslMode = GetEnum(builder, "Ssl Mode", DotRocksSslMode.Disabled);
        bool trustServerCertificate = GetBoolean(builder, "Trust Server Certificate", false);
        X509RevocationMode sslRevocationMode = GetEnum(
            builder,
            "Ssl Revocation Check",
            DefaultSslRevocationMode
        );
        Uri streamLoadEndpoint = GetUri(
            builder,
            "Stream Load Endpoint",
            BuildDefaultStreamLoadEndpoint(server)
        );
        bool allowInsecureStreamLoad = GetBoolean(builder, "Allow Insecure Stream Load", false);
        int maxConnectionRetries = GetInt32(
            builder,
            "Connection Retries",
            DefaultMaxConnectionRetries
        );
        int connectionRetryDelayMs = GetInt32(
            builder,
            "Connection Retry Delay",
            DefaultConnectionRetryDelayMilliseconds
        );
        ArgumentOutOfRangeException.ThrowIfNegative(maxConnectionRetries);
        ArgumentOutOfRangeException.ThrowIfNegative(connectionRetryDelayMs);

        Validate(
            server,
            port,
            userId,
            timeoutSeconds,
            minimumPoolSize,
            maximumPoolSize,
            idleTimeoutSeconds,
            sslMode,
            trustServerCertificate,
            streamLoadEndpoint
        );
        string canonical = BuildConnectionString(
            server,
            port,
            userId,
            password,
            database,
            timeoutSeconds,
            pooling,
            minimumPoolSize,
            maximumPoolSize,
            idleTimeoutSeconds,
            sslMode,
            trustServerCertificate,
            sslRevocationMode,
            streamLoadEndpoint,
            allowInsecureStreamLoad,
            maxConnectionRetries,
            connectionRetryDelayMs
        );

        return new DotRocksConnectionOptions(
            server,
            port,
            userId,
            password,
            database,
            TimeSpan.FromSeconds(timeoutSeconds),
            pooling,
            minimumPoolSize,
            maximumPoolSize,
            TimeSpan.FromSeconds(idleTimeoutSeconds),
            sslMode,
            trustServerCertificate,
            sslRevocationMode,
            streamLoadEndpoint,
            allowInsecureStreamLoad,
            maxConnectionRetries,
            TimeSpan.FromMilliseconds(connectionRetryDelayMs),
            canonical
        );
    }

    public string ToSanitizedString() =>
        BuildConnectionString(
            Server,
            Port,
            UserId,
            "***",
            Database,
            (int)ConnectionTimeout.TotalSeconds,
            Pooling,
            MinimumPoolSize,
            MaximumPoolSize,
            (int)ConnectionIdleTimeout.TotalSeconds,
            SslMode,
            TrustServerCertificate,
            SslRevocationMode,
            StreamLoadEndpoint,
            AllowInsecureStreamLoad,
            MaxConnectionRetries,
            (int)ConnectionRetryDelay.TotalMilliseconds
        );

    internal DotRocksConnectionPoolKey CreatePoolKey() =>
        new(
            Server,
            Port,
            UserId,
            Password,
            Database,
            (int)ConnectionTimeout.TotalSeconds,
            SslMode,
            TrustServerCertificate,
            SslRevocationMode
        );

    private static void Validate(
        string server,
        int port,
        string userId,
        int timeoutSeconds,
        int minimumPoolSize,
        int maximumPoolSize,
        int idleTimeoutSeconds,
        DotRocksSslMode sslMode,
        bool trustServerCertificate,
        Uri streamLoadEndpoint
    )
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            throw new ArgumentException("Server must not be empty.", nameof(server));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                port,
                "Port must be between 1 and 65535."
            );
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID must not be empty.", nameof(userId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumPoolSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPoolSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(idleTimeoutSeconds);
        if (minimumPoolSize > maximumPoolSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumPoolSize),
                minimumPoolSize,
                "Minimum Pool Size must be less than or equal to Maximum Pool Size."
            );
        }

        if (trustServerCertificate && sslMode == DotRocksSslMode.Disabled)
        {
            throw new ArgumentException(
                "Trust Server Certificate requires Ssl Mode=Required.",
                nameof(trustServerCertificate)
            );
        }

        if (!streamLoadEndpoint.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "Stream Load Endpoint must be an absolute URI.",
                nameof(streamLoadEndpoint)
            );
        }

        if (streamLoadEndpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "Stream Load Endpoint must use http or https.",
                nameof(streamLoadEndpoint)
            );
        }

        if (!string.IsNullOrEmpty(streamLoadEndpoint.UserInfo))
        {
            throw new ArgumentException(
                "Stream Load Endpoint must not include user information.",
                nameof(streamLoadEndpoint)
            );
        }
    }

    private static string GetString(
        DbConnectionStringBuilder builder,
        string canonical,
        string fallback
    )
    {
        foreach (string keyword in Aliases(canonical))
        {
            if (builder.TryGetValue(keyword, out object? value))
            {
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                    ?? string.Empty;
            }
        }

        return fallback;
    }

    private static int GetInt32(DbConnectionStringBuilder builder, string canonical, int fallback)
    {
        foreach (string keyword in Aliases(canonical))
        {
            if (builder.TryGetValue(keyword, out object? value))
            {
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return fallback;
    }

    private static bool GetBoolean(
        DbConnectionStringBuilder builder,
        string canonical,
        bool fallback
    )
    {
        foreach (string keyword in Aliases(canonical))
        {
            if (builder.TryGetValue(keyword, out object? value))
            {
                return Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return fallback;
    }

    private static TEnum GetEnum<TEnum>(
        DbConnectionStringBuilder builder,
        string canonical,
        TEnum fallback
    )
        where TEnum : struct, Enum
    {
        foreach (string keyword in Aliases(canonical))
        {
            if (builder.TryGetValue(keyword, out object? value))
            {
                if (value is TEnum typed)
                {
                    return typed;
                }

                string text =
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                    ?? string.Empty;
                if (Enum.TryParse(text, ignoreCase: true, out TEnum parsed))
                {
                    return parsed;
                }

                throw new ArgumentException(
                    $"{canonical} value '{text}' is not supported.",
                    nameof(builder)
                );
            }
        }

        return fallback;
    }

    private static Uri GetUri(DbConnectionStringBuilder builder, string canonical, Uri fallback)
    {
        foreach (string keyword in Aliases(canonical))
        {
            if (builder.TryGetValue(keyword, out object? value))
            {
                string text =
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                    ?? string.Empty;
                return new Uri(text, UriKind.Absolute);
            }
        }

        return fallback;
    }

    internal static Uri BuildDefaultStreamLoadEndpoint(string server) =>
        new($"http://{server}:{DefaultStreamLoadPort}", UriKind.Absolute);

    private static IEnumerable<string> Aliases(string canonical) =>
        canonical switch
        {
            "Server" => ["Server", "Host", "Data Source"],
            "User ID" => ["User ID", "UserID", "User", "Uid", "Username"],
            "Password" => ["Password", "Pwd"],
            "Database" => ["Database", "Initial Catalog"],
            "Connection Timeout" => ["Connection Timeout", "Connect Timeout", "Timeout"],
            "Minimum Pool Size" => ["Minimum Pool Size", "Min Pool Size"],
            "Maximum Pool Size" => ["Maximum Pool Size", "Max Pool Size"],
            "Connection Idle Timeout" => ["Connection Idle Timeout", "Idle Timeout"],
            "Ssl Mode" => ["Ssl Mode", "SSL Mode", "SslMode"],
            "Trust Server Certificate" => ["Trust Server Certificate", "TrustServerCertificate"],
            "Ssl Revocation Check" =>
            [
                "Ssl Revocation Check",
                "SSL Revocation Check",
                "SslRevocationCheck",
                "Revocation",
            ],
            "Allow Insecure Stream Load" =>
            [
                "Allow Insecure Stream Load",
                "AllowInsecureStreamLoad",
                "Allow Insecure StreamLoad",
            ],
            "Stream Load Endpoint" =>
            [
                "Stream Load Endpoint",
                "StreamLoadEndpoint",
                "Stream Load URL",
                "Http Endpoint",
            ],
            "Connection Retries" =>
            [
                "Connection Retries",
                "ConnectionRetries",
                "Connect Retry Count",
            ],
            "Connection Retry Delay" =>
            [
                "Connection Retry Delay",
                "ConnectionRetryDelay",
                "Retry Delay",
            ],
            _ => [canonical],
        };

    private static string BuildConnectionString(
        string server,
        int port,
        string userId,
        string password,
        string database,
        int timeoutSeconds,
        bool pooling,
        int minimumPoolSize,
        int maximumPoolSize,
        int idleTimeoutSeconds,
        DotRocksSslMode sslMode,
        bool trustServerCertificate,
        X509RevocationMode sslRevocationMode,
        Uri streamLoadEndpoint,
        bool allowInsecureStreamLoad,
        int maxConnectionRetries,
        int connectionRetryDelayMilliseconds
    )
    {
        var builder = new StringBuilder();
        Append(builder, "Server", server);
        Append(builder, "Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, "User ID", userId);
        if (password.Length > 0)
        {
            Append(builder, "Password", password);
        }

        if (database.Length > 0)
        {
            Append(builder, "Database", database);
        }

        Append(
            builder,
            "Connection Timeout",
            timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Append(
            builder,
            "Pooling",
            pooling.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Append(
            builder,
            "Minimum Pool Size",
            minimumPoolSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Append(
            builder,
            "Maximum Pool Size",
            maximumPoolSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Append(
            builder,
            "Connection Idle Timeout",
            idleTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Append(builder, "Ssl Mode", sslMode.ToString());
        Append(
            builder,
            "Trust Server Certificate",
            trustServerCertificate.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Append(builder, "Ssl Revocation Check", sslRevocationMode.ToString());
        Append(builder, "Stream Load Endpoint", streamLoadEndpoint.AbsoluteUri);
        Append(
            builder,
            "Allow Insecure Stream Load",
            allowInsecureStreamLoad.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Append(
            builder,
            "Connection Retries",
            maxConnectionRetries.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Append(
            builder,
            "Connection Retry Delay",
            connectionRetryDelayMilliseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string key, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append(';');
        }

        builder.Append(key);
        builder.Append('=');
        builder.Append(value.Replace(";", "\\;", StringComparison.Ordinal));
    }
}

internal sealed record DotRocksConnectionPoolKey(
    string Server,
    int Port,
    string UserId,
    string Password,
    string Database,
    int ConnectionTimeoutSeconds,
    DotRocksSslMode SslMode,
    bool TrustServerCertificate,
    X509RevocationMode SslRevocationMode
)
{
    // Equality/hash still use the real Password (pool identity), but the default record ToString
    // would print it; redact so the key cannot leak the password through logs or diagnostics.
    public override string ToString() =>
        $"DotRocksConnectionPoolKey {{ Server = {Server}, Port = {Port}, UserId = {UserId}, "
        + $"Password = ***, Database = {Database}, ConnectionTimeoutSeconds = {ConnectionTimeoutSeconds}, "
        + $"SslMode = {SslMode}, TrustServerCertificate = {TrustServerCertificate}, "
        + $"SslRevocationMode = {SslRevocationMode} }}";
}
