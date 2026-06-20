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
}
