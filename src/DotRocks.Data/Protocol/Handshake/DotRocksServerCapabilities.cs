namespace DotRocks.Data.Protocol.Handshake;

/// <summary>
/// The driver-visible StarRocks feature gates for a connected server, derived once from the
/// handshake <see cref="DotRocksServerVersion"/>. Features ask named questions
/// (for example <see cref="SupportsMultiTableStreamLoadTransaction"/>) instead of comparing
/// version numbers, so version knowledge lives in exactly one place: the introduced-in thresholds
/// below.
/// </summary>
/// <remarks>
/// Forward compatibility falls out of the threshold comparison: a version newer than the newest
/// known line (a future 5.x) is <c>&gt;=</c> every introduced-in threshold and therefore gains all
/// additive capabilities, while an unrecognized or non-StarRocks version
/// (<see cref="DotRocksServerVersion.IsStarRocks"/> is <see langword="false"/>) is treated as having
/// no capabilities (fail-closed). A capability that StarRocks removes in a future line is modeled by
/// adding a removed-in upper bound at that gate.
/// </remarks>
internal sealed class DotRocksServerCapabilities
{
    // Single source of truth: the StarRocks line each capability was introduced in. Adding support
    // for a new feature is a new threshold here plus a flag below; nothing else in the driver reads
    // raw version numbers.
    private static readonly DotRocksServerVersion HttpSqlApiSince =
        DotRocksServerVersion.ForStarRocks(3, 2, 0);
    private static readonly DotRocksServerVersion MySqlProtocolTlsSince =
        DotRocksServerVersion.ForStarRocks(3, 4, 1);
    private static readonly DotRocksServerVersion SqlTransactionsSince =
        DotRocksServerVersion.ForStarRocks(3, 5, 0);
    private static readonly DotRocksServerVersion StreamLoadPreparedTimeoutSince =
        DotRocksServerVersion.ForStarRocks(3, 5, 4);
    private static readonly DotRocksServerVersion MultiTableStreamLoadTransactionSince =
        DotRocksServerVersion.ForStarRocks(4, 0, 0);
    private static readonly DotRocksServerVersion Decimal256Since =
        DotRocksServerVersion.ForStarRocks(4, 0, 0);

    private DotRocksServerCapabilities(
        DotRocksServerVersion serverVersion,
        DotRocksServerVersion effectiveVersion,
        bool isServerVersionOverridden,
        bool supportsHttpSqlApi,
        bool supportsMySqlProtocolTls,
        bool supportsSqlTransactions,
        bool supportsStreamLoadPreparedTimeout,
        bool supportsMultiTableStreamLoadTransaction,
        bool supportsDecimal256
    )
    {
        ServerVersion = serverVersion;
        EffectiveVersion = effectiveVersion;
        IsServerVersionOverridden = isServerVersionOverridden;
        SupportsHttpSqlApi = supportsHttpSqlApi;
        SupportsMySqlProtocolTls = supportsMySqlProtocolTls;
        SupportsSqlTransactions = supportsSqlTransactions;
        SupportsStreamLoadPreparedTimeout = supportsStreamLoadPreparedTimeout;
        SupportsMultiTableStreamLoadTransaction = supportsMultiTableStreamLoadTransaction;
        SupportsDecimal256 = supportsDecimal256;
    }

    /// <summary>Gets the server version detected from the handshake (always the real server version).</summary>
    public DotRocksServerVersion ServerVersion { get; }

    /// <summary>
    /// Gets the version the capability gates were derived from. Equals <see cref="ServerVersion"/>
    /// unless a <c>Server Compatibility Level</c> override pinned a different version.
    /// </summary>
    public DotRocksServerVersion EffectiveVersion { get; }

    /// <summary>
    /// Gets a value indicating whether a <c>Server Compatibility Level</c> override, rather than the
    /// detected handshake version, determined the capability gates.
    /// </summary>
    public bool IsServerVersionOverridden { get; }

    /// <summary>Gets a value indicating whether the HTTP SQL API is available (StarRocks 3.2+).</summary>
    public bool SupportsHttpSqlApi { get; }

    /// <summary>Gets a value indicating whether MySQL-protocol TLS is available (StarRocks 3.4.1+).</summary>
    public bool SupportsMySqlProtocolTls { get; }

    /// <summary>Gets a value indicating whether SQL transactions are available (StarRocks 3.5+, beta).</summary>
    public bool SupportsSqlTransactions { get; }

    /// <summary>
    /// Gets a value indicating whether the Stream Load transaction <c>prepared_timeout</c> option is
    /// honored (StarRocks 3.5.4+).
    /// </summary>
    public bool SupportsStreamLoadPreparedTimeout { get; }

    /// <summary>
    /// Gets a value indicating whether single-database multi-table Stream Load transactions are
    /// available (StarRocks 4.0+). Earlier lines are single-table only.
    /// </summary>
    public bool SupportsMultiTableStreamLoadTransaction { get; }

    /// <summary>Gets a value indicating whether <c>DECIMAL256</c> is available (StarRocks 4.0+).</summary>
    public bool SupportsDecimal256 { get; }

    /// <summary>
    /// Derives the capability set for <paramref name="detectedVersion"/>. When
    /// <paramref name="compatibilityOverride"/> is supplied (the <c>Server Compatibility Level</c>
    /// escape hatch), the gates are derived from it instead, while <see cref="ServerVersion"/> still
    /// reports the real detected version. An unrecognized effective version yields a set with every
    /// capability disabled.
    /// </summary>
    public static DotRocksServerCapabilities For(
        DotRocksServerVersion detectedVersion,
        DotRocksServerVersion? compatibilityOverride = null
    )
    {
        DotRocksServerVersion effective = compatibilityOverride ?? detectedVersion;
        bool recognized = effective.IsStarRocks;

        return new DotRocksServerCapabilities(
            detectedVersion,
            effective,
            isServerVersionOverridden: compatibilityOverride is not null,
            supportsHttpSqlApi: recognized && effective >= HttpSqlApiSince,
            supportsMySqlProtocolTls: recognized && effective >= MySqlProtocolTlsSince,
            supportsSqlTransactions: recognized && effective >= SqlTransactionsSince,
            supportsStreamLoadPreparedTimeout: recognized
                && effective >= StreamLoadPreparedTimeoutSince,
            supportsMultiTableStreamLoadTransaction: recognized
                && effective >= MultiTableStreamLoadTransactionSince,
            supportsDecimal256: recognized && effective >= Decimal256Since
        );
    }
}
