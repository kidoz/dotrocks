using Microsoft.CodeAnalysis;

namespace DotRocks.Analyzers.Infrastructure;

/// <summary>
/// Diagnostic descriptors for DotRocks analyzers.
/// </summary>
public static class DotRocksDiagnosticDescriptors
{
    /// <summary>
    /// Diagnostic id for insecure Stream Load HTTP endpoints with credentials.
    /// </summary>
    public const string InsecureStreamLoadEndpointDiagnosticId = "DTR0001";

    /// <summary>
    /// Diagnostic id for EF writable keys that are not configured with ValueGeneratedNever().
    /// </summary>
    public const string MissingValueGeneratedNeverDiagnosticId = "DTR0002";

    /// <summary>
    /// Diagnostic id for unsupported EF binary and varbinary mappings.
    /// </summary>
    public const string UnsupportedBinaryMappingDiagnosticId = "DTR0003";

    /// <summary>
    /// Diagnostic id for visible transaction double-completion.
    /// </summary>
    public const string TransactionDoubleCompletionDiagnosticId = "DTR0004";

    /// <summary>
    /// Diagnostic id for unsupported EF EnsureCreated and EnsureDeleted usage.
    /// </summary>
    public const string UnsupportedDatabaseCreatorDiagnosticId = "DTR0005";

    /// <summary>
    /// Diagnostic id for unsupported EF ExecuteUpdate and ExecuteDelete usage.
    /// </summary>
    public const string UnsupportedBulkDmlDiagnosticId = "DTR0006";

    /// <summary>
    /// Diagnostic id for source-visible range changes followed by SaveChanges.
    /// </summary>
    public const string MultiRowSaveChangesDiagnosticId = "DTR0007";

    /// <summary>
    /// Diagnostic id for EF entities configured with an unsupported composite primary key.
    /// </summary>
    public const string CompositePrimaryKeyDiagnosticId = "DTR0008";

    internal static readonly DiagnosticDescriptor InsecureStreamLoadEndpoint = new(
        InsecureStreamLoadEndpointDiagnosticId,
        "Avoid insecure Stream Load endpoints with credentials",
        "Connection string uses an HTTP Stream Load endpoint with credentials",
        "Security",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Use HTTPS when a DotRocks connection string contains Stream Load credentials."
    );

    internal static readonly DiagnosticDescriptor MissingValueGeneratedNever = new(
        MissingValueGeneratedNeverDiagnosticId,
        "Configure EF writable keys with ValueGeneratedNever",
        "Entity key property '{0}' is not configured with ValueGeneratedNever()",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DotRocks EF Core SaveChanges supports explicit values only; writable key properties should be configured with ValueGeneratedNever()."
    );

    internal static readonly DiagnosticDescriptor UnsupportedBinaryMapping = new(
        UnsupportedBinaryMappingDiagnosticId,
        "EF binary mapping is unsupported",
        "DotRocks EF Core does not support '{0}' mapping yet",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Binary and varbinary EF mappings are unsupported until the EF read/write surface is verified end to end."
    );

    internal static readonly DiagnosticDescriptor TransactionDoubleCompletion = new(
        TransactionDoubleCompletionDiagnosticId,
        "Transaction is completed more than once",
        "Transaction variable '{0}' is completed more than once",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DotRocks SQL transactions and Stream Load transactions are single-use after commit or rollback."
    );

    internal static readonly DiagnosticDescriptor UnsupportedDatabaseCreator = new(
        UnsupportedDatabaseCreatorDiagnosticId,
        "EF database creator API is unsupported",
        "DotRocks EF Core does not support '{0}'",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DotRocks EF Core supports migrations for conservative StarRocks DDL; EnsureCreated and EnsureDeleted are explicit unsupported APIs."
    );

    internal static readonly DiagnosticDescriptor UnsupportedBulkDml = new(
        UnsupportedBulkDmlDiagnosticId,
        "EF bulk LINQ DML is unsupported",
        "DotRocks EF Core does not support '{0}'",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DotRocks EF Core does not translate ExecuteUpdate or ExecuteDelete; use tracked single-row SaveChanges or raw SQL with explicit parameters."
    );

    internal static readonly DiagnosticDescriptor MultiRowSaveChanges = new(
        MultiRowSaveChangesDiagnosticId,
        "Avoid multi-row EF SaveChanges",
        "Range change '{0}' followed by SaveChanges may produce unsupported multi-row DML for StarRocks",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "StarRocks rejects a second DML against a table already written in the same SQL transaction; DotRocks EF Core supports one row per SaveChanges for tracked writes."
    );

    internal static readonly DiagnosticDescriptor CompositePrimaryKey = new(
        CompositePrimaryKeyDiagnosticId,
        "Avoid composite primary keys",
        "Entity '{0}' is configured with a composite primary key; DotRocks EF Core requires a single-column primary key",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DotRocks EF Core rejects composite primary keys at model validation. Configure a single-column primary key for writable entities, or HasNoKey() for read-only entities. To fail the build on this configuration, set dotnet_diagnostic.DTR0008.severity = error in .editorconfig."
    );
}
