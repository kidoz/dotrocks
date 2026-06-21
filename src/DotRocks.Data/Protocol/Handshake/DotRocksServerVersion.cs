using System.Globalization;

namespace DotRocks.Data.Protocol.Handshake;

/// <summary>
/// The StarRocks server version reported in the connection handshake, parsed from strings such as
/// <c>8.0.33-StarRocks-3.5.4</c> (the MySQL-compatibility prefix is ignored; the version is the
/// <c>3.5.4</c> after the <c>StarRocks</c> marker). Parsing is deliberately defensive: an
/// unrecognized or non-StarRocks string yields <see cref="Unknown"/> rather than throwing, so the
/// driver can degrade to baseline/probe behavior instead of failing on an unexpected server.
/// </summary>
internal readonly struct DotRocksServerVersion
    : IEquatable<DotRocksServerVersion>,
        IComparable<DotRocksServerVersion>,
        IComparable
{
    /// <summary>The product marker that precedes the StarRocks version in the handshake string.</summary>
    private const string Marker = "StarRocks";

    /// <summary>An unrecognized or non-StarRocks server version, ordered below any recognized version.</summary>
    public static readonly DotRocksServerVersion Unknown = new(false, 0, 0, 0, string.Empty);

    private DotRocksServerVersion(bool isStarRocks, int major, int minor, int patch, string raw)
    {
        IsStarRocks = isStarRocks;
        Major = major;
        Minor = minor;
        Patch = patch;
        Raw = raw;
    }

    /// <summary>
    /// Gets a value indicating whether a StarRocks version with at least a major component was
    /// recognized. When <see langword="false"/>, the numeric components are zero and capability
    /// decisions must fall back to baseline or live probes.
    /// </summary>
    public bool IsStarRocks { get; }

    /// <summary>Gets the major version component (the <c>3</c> in <c>3.5.4</c>).</summary>
    public int Major { get; }

    /// <summary>Gets the minor version component (the <c>5</c> in <c>3.5.4</c>).</summary>
    public int Minor { get; }

    /// <summary>Gets the patch version component (the <c>4</c> in <c>3.5.4</c>); zero when absent.</summary>
    public int Patch { get; }

    /// <summary>Gets the original, unparsed server version string.</summary>
    public string Raw { get; }

    /// <summary>
    /// Parses a handshake server version string. Tolerates a missing <c>StarRocks</c> marker,
    /// absent minor/patch components, and trailing pre-release or build suffixes (for example
    /// <c>4.0.0-rc01</c>). Never throws on malformed server data; an unrecognized string returns a
    /// value equal to <see cref="Unknown"/> while preserving <see cref="Raw"/> for diagnostics.
    /// </summary>
    /// <param name="serverVersion">The raw version string from the server handshake.</param>
    public static DotRocksServerVersion Parse(string serverVersion)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);

        int markerIndex = serverVersion.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return new DotRocksServerVersion(false, 0, 0, 0, serverVersion);
        }

        // Anchor the scan after the marker so the MySQL-compatibility prefix (e.g. "8.0.33")
        // is never mistaken for the StarRocks version. Skip any separator (typically '-').
        int index = markerIndex + Marker.Length;
        while (index < serverVersion.Length && !char.IsAsciiDigit(serverVersion[index]))
        {
            index++;
        }

        Span<int> components = [0, 0, 0];
        int parsed = 0;
        while (
            parsed < 3 && index < serverVersion.Length && char.IsAsciiDigit(serverVersion[index])
        )
        {
            int start = index;
            while (index < serverVersion.Length && char.IsAsciiDigit(serverVersion[index]))
            {
                index++;
            }

            if (
                !int.TryParse(
                    serverVersion.AsSpan(start, index - start),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int value
                )
            )
            {
                // Overflow or otherwise unparsable component: keep the components read so far.
                break;
            }

            components[parsed++] = value;

            if (index < serverVersion.Length && serverVersion[index] == '.')
            {
                index++;
                continue;
            }

            break;
        }

        if (parsed == 0)
        {
            return new DotRocksServerVersion(false, 0, 0, 0, serverVersion);
        }

        return new DotRocksServerVersion(
            true,
            components[0],
            components[1],
            components[2],
            serverVersion
        );
    }

    /// <summary>
    /// Creates a recognized StarRocks version from explicit components. Intended for the
    /// capability derivation table's introduced-in / removed-in thresholds, not for parsing.
    /// </summary>
    public static DotRocksServerVersion ForStarRocks(int major, int minor, int patch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);

        return new DotRocksServerVersion(
            true,
            major,
            minor,
            patch,
            string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.{patch}")
        );
    }

    /// <inheritdoc />
    public int CompareTo(DotRocksServerVersion other)
    {
        int byMarker = IsStarRocks.CompareTo(other.IsStarRocks);
        if (byMarker != 0)
        {
            return byMarker;
        }

        int byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0)
        {
            return byMajor;
        }

        int byMinor = Minor.CompareTo(other.Minor);
        if (byMinor != 0)
        {
            return byMinor;
        }

        return Patch.CompareTo(other.Patch);
    }

    /// <inheritdoc />
    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        if (obj is DotRocksServerVersion other)
        {
            return CompareTo(other);
        }

        throw new ArgumentException(
            $"Object must be of type {nameof(DotRocksServerVersion)}.",
            nameof(obj)
        );
    }

    /// <summary>
    /// Compares the recognized version identity (<see cref="IsStarRocks"/> and the numeric
    /// components). The diagnostic <see cref="Raw"/> string is not part of equality, so any two
    /// unrecognized strings compare equal to <see cref="Unknown"/>.
    /// </summary>
    public bool Equals(DotRocksServerVersion other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DotRocksServerVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsStarRocks, Major, Minor, Patch);

    /// <inheritdoc />
    public override string ToString() =>
        Raw.Length > 0
            ? Raw
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public static bool operator ==(DotRocksServerVersion left, DotRocksServerVersion right) =>
        left.Equals(right);

    public static bool operator !=(DotRocksServerVersion left, DotRocksServerVersion right) =>
        !left.Equals(right);

    public static bool operator <(DotRocksServerVersion left, DotRocksServerVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(DotRocksServerVersion left, DotRocksServerVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(DotRocksServerVersion left, DotRocksServerVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(DotRocksServerVersion left, DotRocksServerVersion right) =>
        left.CompareTo(right) >= 0;
}
