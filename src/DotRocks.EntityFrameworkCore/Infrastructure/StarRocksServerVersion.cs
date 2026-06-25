using System.Data.Common;
using System.Globalization;

namespace DotRocks.EntityFrameworkCore.Infrastructure;

/// <summary>
/// A StarRocks server version used to gate version-specific provider behavior. Configure it
/// explicitly with <see cref="DotRocksDbContextOptionsBuilder.ServerVersion(StarRocksServerVersion)"/>;
/// constructing <see cref="Microsoft.EntityFrameworkCore.DbContextOptions"/> never contacts the
/// server. To discover the version
/// once at startup, call <see cref="DetectAsync(string, System.Threading.CancellationToken)"/> and
/// cache the result.
/// </summary>
public sealed class StarRocksServerVersion : IEquatable<StarRocksServerVersion>
{
    /// <summary>Initializes a new instance of the <see cref="StarRocksServerVersion"/> class.</summary>
    /// <param name="major">The major version component (for example the <c>4</c> in <c>4.0.7</c>).</param>
    /// <param name="minor">The minor version component.</param>
    /// <param name="patch">The patch version component.</param>
    public StarRocksServerVersion(int major, int minor = 0, int patch = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);

        Major = major;
        Minor = minor;
        Patch = patch;
    }

    /// <summary>Gets the major version component.</summary>
    public int Major { get; }

    /// <summary>Gets the minor version component.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch version component.</summary>
    public int Patch { get; }

    /// <summary>
    /// Parses a StarRocks build string such as <c>3.5.5-fd4e51b</c> into a
    /// <see cref="StarRocksServerVersion"/>, ignoring any trailing build or pre-release suffix.
    /// </summary>
    /// <param name="version">The version string to parse.</param>
    /// <exception cref="FormatException">The string does not begin with a numeric version component.</exception>
    public static StarRocksServerVersion Parse(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        // Take the leading "major.minor.patch" run and drop any "-build" / "-rc" suffix.
        string trimmed = version.Trim();
        int suffixIndex = trimmed.IndexOfAny(['-', ' ']);
        string core = suffixIndex < 0 ? trimmed : trimmed[..suffixIndex];

        string[] parts = core.Split('.');
        if (
            parts.Length is < 1 or > 3
            || !TryParseComponent(parts, 0, out int major)
            || !TryParseComponent(parts, 1, out int minor)
            || !TryParseComponent(parts, 2, out int patch)
        )
        {
            throw new FormatException($"'{version}' is not a recognized StarRocks version string.");
        }

        return new StarRocksServerVersion(major, minor, patch);
    }

    /// <summary>
    /// Opens a short-lived connection, reads <c>SELECT current_version()</c>, and returns the
    /// detected <see cref="StarRocksServerVersion"/>. This performs network I/O, so it is opt-in:
    /// call it explicitly at startup, cache the result, and pass it to
    /// <see cref="DotRocksDbContextOptionsBuilder.ServerVersion(StarRocksServerVersion)"/>.
    /// </summary>
    /// <param name="connectionString">The DotRocks connection string.</param>
    /// <param name="cancellationToken">A token to cancel the detection.</param>
    /// <exception cref="InvalidOperationException">The server did not return a version string.</exception>
    public static async Task<StarRocksServerVersion> DetectAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var connection = new Data.DotRocksConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            DbCommand command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT current_version()";
                object? result = await command
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (result is not string version || string.IsNullOrWhiteSpace(version))
                {
                    throw new InvalidOperationException(
                        "StarRocks did not return a value from SELECT current_version()."
                    );
                }

                return Parse(version);
            }
        }
    }

    /// <inheritdoc />
    public bool Equals(StarRocksServerVersion? other) =>
        other is not null && Major == other.Major && Minor == other.Minor && Patch == other.Patch;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as StarRocksServerVersion);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static bool TryParseComponent(string[] parts, int index, out int value)
    {
        if (index >= parts.Length)
        {
            value = 0;
            return true;
        }

        return int.TryParse(
            parts[index],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value
        );
    }
}
