namespace DotRocks.FlightSql;

/// <summary>
/// Configures a StarRocks Arrow Flight SQL data source.
/// </summary>
public sealed class DotRocksFlightSqlOptions
{
    /// <summary>
    /// Initializes options for a StarRocks Arrow Flight SQL frontend endpoint.
    /// </summary>
    /// <param name="endpoint">The frontend Flight SQL endpoint.</param>
    /// <param name="userName">The StarRocks user name.</param>
    /// <param name="password">The StarRocks password.</param>
    public DotRocksFlightSqlOptions(Uri endpoint, string userName, string password)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        UserName = userName ?? throw new ArgumentNullException(nameof(userName));
        Password = password ?? throw new ArgumentNullException(nameof(password));
    }

    /// <summary>
    /// Gets the frontend Flight SQL endpoint.
    /// </summary>
    public Uri Endpoint { get; }

    /// <summary>
    /// Gets the StarRocks user name.
    /// </summary>
    public string UserName { get; }

    internal string Password { get; }

    /// <summary>
    /// Gets or initializes the maximum duration of query discovery or result streaming.
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or initializes whether plaintext Flight endpoints are allowed.
    /// </summary>
    /// <remarks>
    /// Plaintext transport exposes credentials and query results to network observers. Enable it
    /// only on a trusted network or for local development.
    /// </remarks>
    public bool AllowInsecureTransport { get; init; }

    /// <summary>
    /// Gets or initializes additional endpoint addresses that may receive the query ticket and
    /// authorization header.
    /// </summary>
    /// <remarks>
    /// The exact frontend address is always allowed. StarRocks versions before frontend proxy mode
    /// can return backend addresses here; list each trusted backend scheme, host, and port
    /// explicitly.
    /// </remarks>
    public IReadOnlyCollection<Uri> AllowedEndpointAddresses { get; init; } = [];

    /// <inheritdoc />
    public override string ToString()
    {
        string endpoint = Endpoint.IsAbsoluteUri
            ? $"{Endpoint.Scheme}://{Endpoint.Host}:{Endpoint.Port}"
            : "<invalid>";
        return $"Endpoint={endpoint};UserName={UserName};Password=<redacted>;CommandTimeout={CommandTimeout}";
    }
}
