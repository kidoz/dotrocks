using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;

namespace DotRocks.Data;

/// <summary>
/// Builds and parses DotRocks connection strings.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification = "ADO.NET connection string builders conventionally end with ConnectionStringBuilder."
)]
public sealed class DotRocksConnectionStringBuilder
    : DbConnectionStringBuilder,
        IReadOnlyCollection<KeyValuePair<string, object?>>
{
    private const string ServerKeyword = "Server";
    private const string PortKeyword = "Port";
    private const string UserIdKeyword = "User ID";
    private const string PasswordKeyword = "Password";
    private const string DatabaseKeyword = "Database";
    private const string ConnectionTimeoutKeyword = "Connection Timeout";
    private const string PoolingKeyword = "Pooling";
    private const string MinimumPoolSizeKeyword = "Minimum Pool Size";
    private const string MaximumPoolSizeKeyword = "Maximum Pool Size";
    private const string ConnectionIdleTimeoutKeyword = "Connection Idle Timeout";
    private const string SslModeKeyword = "Ssl Mode";
    private const string TrustServerCertificateKeyword = "Trust Server Certificate";
    private const string SslRevocationCheckKeyword = "Ssl Revocation Check";
    private const string StreamLoadEndpointKeyword = "Stream Load Endpoint";
    private const string AllowInsecureStreamLoadKeyword = "Allow Insecure Stream Load";
    private const string ConnectionRetriesKeyword = "Connection Retries";
    private const string ConnectionRetryDelayKeyword = "Connection Retry Delay";
    private const string ConnectionLifetimeKeyword = "Connection Lifetime";

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksConnectionStringBuilder"/> class.
    /// </summary>
    public DotRocksConnectionStringBuilder()
    {
        Server = DotRocksConnectionOptions.DefaultServer;
        Port = DotRocksConnectionOptions.DefaultPort;
        UserId = DotRocksConnectionOptions.DefaultUserId;
        ConnectionTimeout = DotRocksConnectionOptions.DefaultConnectionTimeoutSeconds;
        Pooling = DotRocksConnectionOptions.DefaultPooling;
        MinimumPoolSize = DotRocksConnectionOptions.DefaultMinimumPoolSize;
        MaximumPoolSize = DotRocksConnectionOptions.DefaultMaximumPoolSize;
        ConnectionIdleTimeout = DotRocksConnectionOptions.DefaultConnectionIdleTimeoutSeconds;
        SslMode = DotRocksSslMode.Preferred;
        TrustServerCertificate = false;
        SslRevocationCheck = DotRocksConnectionOptions.DefaultSslRevocationMode;
        AllowInsecureStreamLoad = false;
        ConnectionRetries = DotRocksConnectionOptions.DefaultMaxConnectionRetries;
        ConnectionRetryDelay = DotRocksConnectionOptions.DefaultConnectionRetryDelayMilliseconds;
        ConnectionLifetime = DotRocksConnectionOptions.DefaultConnectionLifetimeSeconds;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksConnectionStringBuilder"/> class from a
    /// connection string.
    /// </summary>
    /// <param name="connectionString">The connection string to parse.</param>
    public DotRocksConnectionStringBuilder(string connectionString)
        : this()
    {
        ConnectionString = connectionString;
    }

    /// <summary>
    /// Gets or sets the StarRocks FE host name or IP address.
    /// </summary>
    public string Server
    {
        get => GetString(ServerKeyword, DotRocksConnectionOptions.DefaultServer);
        set => SetValue(ServerKeyword, value);
    }

    /// <summary>
    /// Gets or sets the StarRocks FE query port.
    /// </summary>
    public int Port
    {
        get => GetInt32(PortKeyword, DotRocksConnectionOptions.DefaultPort);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 65535);
            this[PortKeyword] = value;
        }
    }

    /// <summary>
    /// Gets or sets the StarRocks user name.
    /// </summary>
    public string UserId
    {
        get => GetString(UserIdKeyword, DotRocksConnectionOptions.DefaultUserId);
        set => SetValue(UserIdKeyword, value);
    }

    /// <summary>
    /// Gets or sets the password used for authentication.
    /// </summary>
    public string Password
    {
        get => GetString(PasswordKeyword, string.Empty);
        set => SetValue(PasswordKeyword, value);
    }

    /// <summary>
    /// Gets or sets the default database.
    /// </summary>
    public string Database
    {
        get => GetString(DatabaseKeyword, string.Empty);
        set => SetValue(DatabaseKeyword, value);
    }

    /// <summary>
    /// Gets or sets the connection timeout, in seconds.
    /// </summary>
    public int ConnectionTimeout
    {
        get =>
            GetInt32(
                ConnectionTimeoutKeyword,
                DotRocksConnectionOptions.DefaultConnectionTimeoutSeconds
            );
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            this[ConnectionTimeoutKeyword] = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether physical StarRocks connections are pooled.
    /// </summary>
    public bool Pooling
    {
        get => GetBoolean(PoolingKeyword, DotRocksConnectionOptions.DefaultPooling);
        set => this[PoolingKeyword] = value;
    }

    /// <summary>
    /// Gets or sets the minimum number of idle physical connections retained per pool.
    /// </summary>
    public int MinimumPoolSize
    {
        get => GetInt32(MinimumPoolSizeKeyword, DotRocksConnectionOptions.DefaultMinimumPoolSize);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this[MinimumPoolSizeKeyword] = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of concurrent leased physical connections per pool.
    /// </summary>
    public int MaximumPoolSize
    {
        get => GetInt32(MaximumPoolSizeKeyword, DotRocksConnectionOptions.DefaultMaximumPoolSize);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            this[MaximumPoolSizeKeyword] = value;
        }
    }

    /// <summary>
    /// Gets or sets how long an idle physical connection can remain in the pool, in seconds.
    /// </summary>
    public int ConnectionIdleTimeout
    {
        get =>
            GetInt32(
                ConnectionIdleTimeoutKeyword,
                DotRocksConnectionOptions.DefaultConnectionIdleTimeoutSeconds
            );
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            this[ConnectionIdleTimeoutKeyword] = value;
        }
    }

    /// <summary>
    /// Gets or sets the SQL protocol TLS mode.
    /// </summary>
    public DotRocksSslMode SslMode
    {
        get => GetEnum(SslModeKeyword, DotRocksSslMode.Preferred);
        set => this[SslModeKeyword] = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether TLS certificate validation is bypassed. Requires
    /// <see cref="SslMode"/> to be <see cref="DotRocksSslMode.Required"/>; otherwise opening the
    /// connection throws, since the setting would be a silent no-op on a plaintext fallback.
    /// </summary>
    public bool TrustServerCertificate
    {
        get => GetBoolean(TrustServerCertificateKeyword, false);
        set => this[TrustServerCertificateKeyword] = value;
    }

    /// <summary>
    /// Gets or sets the TLS certificate revocation check mode. Defaults to
    /// <see cref="X509RevocationMode.Offline"/> to avoid a blocking revocation fetch.
    /// </summary>
    public X509RevocationMode SslRevocationCheck
    {
        get =>
            GetEnum(SslRevocationCheckKeyword, DotRocksConnectionOptions.DefaultSslRevocationMode);
        set => this[SslRevocationCheckKeyword] = value;
    }

    /// <summary>
    /// Gets or sets the StarRocks FE HTTP endpoint used for Stream Load requests.
    /// </summary>
    public string StreamLoadEndpoint
    {
        get
        {
            string endpoint = GetString(
                StreamLoadEndpointKeyword,
                DotRocksConnectionOptions.BuildDefaultStreamLoadEndpoint(Server).AbsoluteUri
            );
            return new Uri(endpoint, UriKind.Absolute).AbsoluteUri;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Uri endpoint = new(value, UriKind.Absolute);
            if (endpoint.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException(
                    "Stream Load Endpoint must use http or https.",
                    nameof(value)
                );
            }

            if (!string.IsNullOrEmpty(endpoint.UserInfo))
            {
                throw new ArgumentException(
                    "Stream Load Endpoint must not include user information.",
                    nameof(value)
                );
            }

            this[StreamLoadEndpointKeyword] = endpoint.AbsoluteUri;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether HTTP Stream Load endpoints are allowed.
    /// </summary>
    public bool AllowInsecureStreamLoad
    {
        get => GetBoolean(AllowInsecureStreamLoadKeyword, false);
        set => this[AllowInsecureStreamLoadKeyword] = value;
    }

    /// <summary>
    /// Gets or sets how many times opening a connection is retried on a transient failure.
    /// Defaults to 0 (no retries).
    /// </summary>
    public int ConnectionRetries
    {
        get =>
            GetInt32(
                ConnectionRetriesKeyword,
                DotRocksConnectionOptions.DefaultMaxConnectionRetries
            );
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this[ConnectionRetriesKeyword] = value;
        }
    }

    /// <summary>
    /// Gets or sets the delay between connection-open retries, in milliseconds.
    /// </summary>
    public int ConnectionRetryDelay
    {
        get =>
            GetInt32(
                ConnectionRetryDelayKeyword,
                DotRocksConnectionOptions.DefaultConnectionRetryDelayMilliseconds
            );
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this[ConnectionRetryDelayKeyword] = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum lifetime of a pooled physical connection, in seconds. A returned
    /// connection older than this is discarded instead of reused. Zero (the default) keeps
    /// connections for an unbounded lifetime. A small random jitter is applied per connection so
    /// connections do not all expire together.
    /// </summary>
    public int ConnectionLifetime
    {
        get =>
            GetInt32(
                ConnectionLifetimeKeyword,
                DotRocksConnectionOptions.DefaultConnectionLifetimeSeconds
            );
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this[ConnectionLifetimeKeyword] = value;
        }
    }

    /// <summary>
    /// Gets a sanitized connection-string representation.
    /// </summary>
    /// <returns>A connection string with the password redacted.</returns>
    public override string ToString() => DotRocksConnectionOptions.Parse(this).ToSanitizedString();

    internal DotRocksConnectionOptions BuildOptions() => DotRocksConnectionOptions.Parse(this);

    IEnumerator<KeyValuePair<string, object?>> IEnumerable<
        KeyValuePair<string, object?>
    >.GetEnumerator()
    {
        foreach (string key in Keys)
        {
            yield return new KeyValuePair<string, object?>(key, this[key]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, object?>>)this).GetEnumerator();

    // Alias lookup and value conversion live in DotRocksConnectionStringKeywords, shared with
    // DotRocksConnectionOptions so the two parsers cannot drift apart.
    private string GetString(string keyword, string fallback) =>
        DotRocksConnectionStringKeywords.GetString(this, keyword, fallback);

    private int GetInt32(string keyword, int fallback) =>
        DotRocksConnectionStringKeywords.GetInt32(this, keyword, fallback);

    private bool GetBoolean(string keyword, bool fallback) =>
        DotRocksConnectionStringKeywords.GetBoolean(this, keyword, fallback);

    private TEnum GetEnum<TEnum>(string keyword, TEnum fallback)
        where TEnum : struct, Enum =>
        DotRocksConnectionStringKeywords.GetEnum(this, keyword, fallback);

    private void SetValue(string keyword, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        this[keyword] = value;
    }
}
