using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotRocks.Data.Protocol.Handshake;
using static DotRocks.Data.DotRocksConnectionStringKeywords;

namespace DotRocks.Data;

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
    string ConnectionString,
    DotRocksServerVersion? ServerCompatibilityLevel,
    TimeSpan ConnectionLifetime
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
    public const int DefaultConnectionLifetimeSeconds = 0;

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
            DotRocksSslMode.Preferred,
            false,
            DefaultSslRevocationMode,
            BuildDefaultStreamLoadEndpoint(DefaultServer),
            false,
            DefaultMaxConnectionRetries,
            TimeSpan.FromMilliseconds(DefaultConnectionRetryDelayMilliseconds),
            string.Empty,
            null,
            TimeSpan.FromSeconds(DefaultConnectionLifetimeSeconds)
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
        DotRocksSslMode sslMode = GetEnum(builder, "Ssl Mode", DotRocksSslMode.Preferred);
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
        int connectionLifetimeSeconds = GetInt32(
            builder,
            "Connection Lifetime",
            DefaultConnectionLifetimeSeconds
        );
        ArgumentOutOfRangeException.ThrowIfNegative(maxConnectionRetries);
        ArgumentOutOfRangeException.ThrowIfNegative(connectionRetryDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(connectionLifetimeSeconds);
        DotRocksServerVersion? serverCompatibilityLevel = GetCompatibilityLevel(builder);

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
        var options = new DotRocksConnectionOptions(
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
            string.Empty,
            serverCompatibilityLevel,
            TimeSpan.FromSeconds(connectionLifetimeSeconds)
        );

        // The canonical string is derived from the parsed values, so compute it from the
        // constructed record instead of a third parallel argument list.
        return options with
        {
            ConnectionString = options.BuildConnectionString(password),
        };
    }

    // The compiler-generated record ToString would print Password and the full cleartext
    // ConnectionString. Override it so neither can leak through interpolation, logging, or a
    // debugger; the sanitized form redacts the password.
    public override string ToString() => ToSanitizedString();

    public string ToSanitizedString() => BuildConnectionString("***");

    // The public ADO.NET ConnectionString getter must not return the password (the ODBC/ADO
    // PersistSecurityInfo=false convention): the password is omitted entirely so logging the getter
    // cannot leak the secret. The full credentialed string remains internal for pool keying.
    public string ToRedactedConnectionString() => BuildConnectionString(string.Empty);

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
            SslRevocationMode,
            ServerCompatibilityLevel,
            Pooling,
            MinimumPoolSize,
            MaximumPoolSize,
            (int)ConnectionIdleTimeout.TotalSeconds,
            (int)ConnectionLifetime.TotalSeconds
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

        if (trustServerCertificate && sslMode != DotRocksSslMode.Required)
        {
            // Bypassing certificate validation is only coherent when TLS is guaranteed. Under
            // Preferred it would be a silent no-op whenever the connection falls back to plaintext,
            // so require an explicit Ssl Mode=Required.
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

    private static DotRocksServerVersion? GetCompatibilityLevel(DbConnectionStringBuilder builder)
    {
        if (!TryGetValue(builder, "Server Compatibility Level", out object? value))
        {
            return null;
        }

        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!DotRocksServerVersion.TryParseLevel(text, out DotRocksServerVersion version))
        {
            throw new ArgumentException(
                $"Server Compatibility Level value '{text}' is not a valid StarRocks version.",
                nameof(builder)
            );
        }

        return version;
    }

    internal static Uri BuildDefaultStreamLoadEndpoint(string server) =>
        new($"http://{server}:{DefaultStreamLoadPort}", UriKind.Absolute);

    // Serializes this record back to its canonical connection-string form; only the password
    // rendering varies by caller (real value, "***", or omitted), so it is the single parameter.
    private string BuildConnectionString(string password)
    {
        var builder = new StringBuilder();
        Append(builder, "Server", Server);
        Append(builder, "Port", Port.ToString(CultureInfo.InvariantCulture));
        Append(builder, "User ID", UserId);
        if (password.Length > 0)
        {
            Append(builder, "Password", password);
        }

        if (Database.Length > 0)
        {
            Append(builder, "Database", Database);
        }

        Append(
            builder,
            "Connection Timeout",
            ((int)ConnectionTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)
        );
        Append(builder, "Pooling", Pooling.ToString(CultureInfo.InvariantCulture));
        Append(
            builder,
            "Minimum Pool Size",
            MinimumPoolSize.ToString(CultureInfo.InvariantCulture)
        );
        Append(
            builder,
            "Maximum Pool Size",
            MaximumPoolSize.ToString(CultureInfo.InvariantCulture)
        );
        Append(
            builder,
            "Connection Idle Timeout",
            ((int)ConnectionIdleTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)
        );
        Append(builder, "Ssl Mode", SslMode.ToString());
        Append(
            builder,
            "Trust Server Certificate",
            TrustServerCertificate.ToString(CultureInfo.InvariantCulture)
        );
        Append(builder, "Ssl Revocation Check", SslRevocationMode.ToString());
        Append(builder, "Stream Load Endpoint", StreamLoadEndpoint.AbsoluteUri);
        Append(
            builder,
            "Allow Insecure Stream Load",
            AllowInsecureStreamLoad.ToString(CultureInfo.InvariantCulture)
        );
        Append(
            builder,
            "Connection Retries",
            MaxConnectionRetries.ToString(CultureInfo.InvariantCulture)
        );
        Append(
            builder,
            "Connection Retry Delay",
            ((int)ConnectionRetryDelay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
        );
        if (ServerCompatibilityLevel is { } level)
        {
            Append(builder, "Server Compatibility Level", level.Raw);
        }

        Append(
            builder,
            "Connection Lifetime",
            ((int)ConnectionLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture)
        );

        return builder.ToString();
    }

    // Delegate quoting to DbConnectionStringBuilder so serialization matches the parser exactly:
    // it double-quotes values containing delimiters, quotes, or edge whitespace. The previous
    // hand-rolled backslash escape ("\;") is not a DbConnectionStringBuilder construct, so an
    // escaped value reparsed as separate keywords (option injection — e.g. a Database value could
    // smuggle "...;Ssl Mode=Disabled" into the canonical string and downgrade TLS).
    private static void Append(StringBuilder builder, string key, string value) =>
        DbConnectionStringBuilder.AppendKeyValuePair(builder, key, value);
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
    X509RevocationMode SslRevocationMode,
    DotRocksServerVersion? ServerCompatibilityLevel,
    bool Pooling,
    int MinimumPoolSize,
    int MaximumPoolSize,
    int ConnectionIdleTimeoutSeconds,
    int ConnectionLifetimeSeconds
)
{
    // Equality/hash still use the real Password (pool identity), but the default record ToString
    // would print it; redact so the key cannot leak the password through logs or diagnostics.
    // The compatibility level is part of identity so a pooled connection's computed capabilities
    // are never reused under a different override.
    public override string ToString() =>
        $"DotRocksConnectionPoolKey {{ Server = {Server}, Port = {Port}, UserId = {UserId}, "
        + $"Password = ***, Database = {Database}, ConnectionTimeoutSeconds = {ConnectionTimeoutSeconds}, "
        + $"SslMode = {SslMode}, TrustServerCertificate = {TrustServerCertificate}, "
        + $"SslRevocationMode = {SslRevocationMode}, "
        + $"ServerCompatibilityLevel = {ServerCompatibilityLevel?.Raw ?? "(auto)"}, "
        + $"Pooling = {Pooling}, MinimumPoolSize = {MinimumPoolSize}, "
        + $"MaximumPoolSize = {MaximumPoolSize}, "
        + $"ConnectionIdleTimeoutSeconds = {ConnectionIdleTimeoutSeconds}, "
        + $"ConnectionLifetimeSeconds = {ConnectionLifetimeSeconds} }}";
}
