namespace DotRocks.FlightSql;

internal sealed class FlightSqlEndpointPolicy
{
    private const string ReuseConnectionLocation = "arrow-flight-reuse-connection://?";
    private readonly bool _allowInsecureTransport;
    private readonly HashSet<string> _allowedAddresses;

    public FlightSqlEndpointPolicy(DotRocksFlightSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        PrimaryAddress = NormalizeAddress(options.Endpoint, options.AllowInsecureTransport);
        _allowInsecureTransport = options.AllowInsecureTransport;
        _allowedAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PrimaryAddress.AbsoluteUri,
        };

        foreach (Uri address in options.AllowedEndpointAddresses)
        {
            ArgumentNullException.ThrowIfNull(address);
            Uri normalized = NormalizeAddress(address, options.AllowInsecureTransport);
            _allowedAddresses.Add(normalized.AbsoluteUri);
        }
    }

    public Uri PrimaryAddress { get; }

    public Uri Resolve(string? location)
    {
        if (
            string.IsNullOrEmpty(location)
            || location.Equals(ReuseConnectionLocation, StringComparison.OrdinalIgnoreCase)
        )
        {
            return PrimaryAddress;
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out Uri? endpoint))
        {
            throw new InvalidOperationException(
                "The Flight SQL server returned an invalid endpoint location."
            );
        }

        Uri normalized = NormalizeAddress(endpoint, _allowInsecureTransport);
        if (!_allowedAddresses.Contains(normalized.AbsoluteUri))
        {
            throw new InvalidOperationException(
                "The Flight SQL server returned an endpoint address that is not trusted."
            );
        }

        return normalized;
    }

    private static void ValidateOptions(DotRocksFlightSqlOptions options)
    {
        if (!options.Endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The Flight SQL endpoint must be an absolute URI.",
                nameof(options)
            );
        }

        if (string.IsNullOrWhiteSpace(options.UserName))
        {
            throw new ArgumentException(
                "The Flight SQL user name cannot be empty.",
                nameof(options)
            );
        }

        if (options.UserName.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Flight SQL user name cannot contain a colon.",
                nameof(options)
            );
        }

        if (
            options.CommandTimeout <= TimeSpan.Zero
            || options.CommandTimeout > TimeSpan.FromDays(1)
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The Flight SQL command timeout must be greater than zero and no more than one day."
            );
        }

        ArgumentNullException.ThrowIfNull(options.AllowedEndpointAddresses);
    }

    private static Uri NormalizeAddress(Uri endpoint, bool allowInsecureTransport)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("Flight SQL endpoints must be absolute URIs.");
        }

        if (string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ArgumentException("Flight SQL endpoints must include a host name.");
        }

        if (
            !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.IsNullOrEmpty(endpoint.Query)
            || (endpoint.AbsolutePath.Length > 0 && endpoint.AbsolutePath != "/")
        )
        {
            throw new ArgumentException(
                "Flight SQL endpoints cannot contain user information, paths, queries, or fragments."
            );
        }

        string scheme;
        if (
            endpoint.Scheme.Equals("grpc", StringComparison.OrdinalIgnoreCase)
            || endpoint.Scheme.Equals("grpc+tcp", StringComparison.OrdinalIgnoreCase)
            || endpoint.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
        )
        {
            scheme = "http";
        }
        else if (
            endpoint.Scheme.Equals("grpc+tls", StringComparison.OrdinalIgnoreCase)
            || endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
        )
        {
            scheme = "https";
        }
        else
        {
            throw new ArgumentException("The Flight SQL endpoint uses an unsupported URI scheme.");
        }

        if (scheme == "http" && !allowInsecureTransport)
        {
            throw new InvalidOperationException(
                "Plaintext Flight SQL requires AllowInsecureTransport to be enabled explicitly."
            );
        }

        var builder = new UriBuilder(endpoint)
        {
            Scheme = scheme,
            Path = string.Empty,
            Query = string.Empty,
        };
        return builder.Uri;
    }
}
