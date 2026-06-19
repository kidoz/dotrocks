using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DotRocks.Data.Loading;

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

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksConnectionStringBuilder"/> class.
    /// </summary>
    public DotRocksConnectionStringBuilder()
    {
        Server = DotRocksConnectionOptions.DefaultServer;
        Port = DotRocksConnectionOptions.DefaultPort;
        UserId = DotRocksConnectionOptions.DefaultUserId;
        ConnectionTimeout = DotRocksConnectionOptions.DefaultConnectionTimeoutSeconds;
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

    private string GetString(string keyword, string fallback) =>
        TryGetAliasedValue(keyword, out object? value)
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty
            : fallback;

    private int GetInt32(string keyword, int fallback) =>
        TryGetAliasedValue(keyword, out object? value)
            ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;

    private void SetValue(string keyword, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        this[keyword] = value;
    }

    private bool TryGetAliasedValue(string keyword, out object? value)
    {
        foreach (string alias in Aliases(keyword))
        {
            if (TryGetValue(alias, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static IEnumerable<string> Aliases(string keyword) =>
        keyword switch
        {
            ServerKeyword => [ServerKeyword, "Host", "Data Source"],
            UserIdKeyword => [UserIdKeyword, "UserID", "User", "Uid", "Username"],
            PasswordKeyword => [PasswordKeyword, "Pwd"],
            DatabaseKeyword => [DatabaseKeyword, "Initial Catalog"],
            ConnectionTimeoutKeyword => [ConnectionTimeoutKeyword, "Connect Timeout", "Timeout"],
            _ => [keyword],
        };
}
