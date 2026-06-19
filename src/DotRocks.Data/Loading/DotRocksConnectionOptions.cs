using System.Data.Common;
using System.Text;

namespace DotRocks.Data.Loading;

internal sealed record DotRocksConnectionOptions(
    string Server,
    int Port,
    string UserId,
    string Password,
    string Database,
    TimeSpan ConnectionTimeout,
    string ConnectionString
)
{
    public const string DefaultServer = "127.0.0.1";
    public const int DefaultPort = 9030;
    public const string DefaultUserId = "root";
    public const int DefaultConnectionTimeoutSeconds = 15;

    public static DotRocksConnectionOptions Default { get; } =
        new(
            DefaultServer,
            DefaultPort,
            DefaultUserId,
            string.Empty,
            string.Empty,
            TimeSpan.FromSeconds(DefaultConnectionTimeoutSeconds),
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

        Validate(server, port, userId, timeoutSeconds);
        string canonical = BuildConnectionString(
            server,
            port,
            userId,
            password,
            database,
            timeoutSeconds
        );

        return new DotRocksConnectionOptions(
            server,
            port,
            userId,
            password,
            database,
            TimeSpan.FromSeconds(timeoutSeconds),
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
            (int)ConnectionTimeout.TotalSeconds
        );

    private static void Validate(string server, int port, string userId, int timeoutSeconds)
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

    private static IEnumerable<string> Aliases(string canonical) =>
        canonical switch
        {
            "Server" => ["Server", "Host", "Data Source"],
            "User ID" => ["User ID", "UserID", "User", "Uid", "Username"],
            "Password" => ["Password", "Pwd"],
            "Database" => ["Database", "Initial Catalog"],
            "Connection Timeout" => ["Connection Timeout", "Connect Timeout", "Timeout"],
            _ => [canonical],
        };

    private static string BuildConnectionString(
        string server,
        int port,
        string userId,
        string password,
        string database,
        int timeoutSeconds
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
