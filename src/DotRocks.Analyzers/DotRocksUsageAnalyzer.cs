using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers;

/// <summary>
/// Reports insecure or unsupported DotRocks usage patterns that are visible in source.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DotRocksUsageAnalyzer : DiagnosticAnalyzer
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

    private static readonly DiagnosticDescriptor InsecureStreamLoadEndpointRule = new(
        InsecureStreamLoadEndpointDiagnosticId,
        "Avoid insecure Stream Load endpoints with credentials",
        "Connection string uses an HTTP Stream Load endpoint with credentials",
        "Security",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Use HTTPS when a DotRocks connection string contains Stream Load credentials."
    );

    private static readonly DiagnosticDescriptor MissingValueGeneratedNeverRule = new(
        MissingValueGeneratedNeverDiagnosticId,
        "Configure EF writable keys with ValueGeneratedNever",
        "Entity model configures a key but no ValueGeneratedNever() call is visible",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DotRocks EF Core SaveChanges supports explicit values only; writable key properties should be configured with ValueGeneratedNever()."
    );

    private static readonly DiagnosticDescriptor UnsupportedBinaryMappingRule = new(
        UnsupportedBinaryMappingDiagnosticId,
        "EF binary mapping is unsupported",
        "DotRocks EF Core does not support '{0}' mapping yet",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Binary and varbinary EF mappings are unsupported until the EF read/write surface is verified end to end."
    );

    private static readonly DiagnosticDescriptor TransactionDoubleCompletionRule = new(
        TransactionDoubleCompletionDiagnosticId,
        "Transaction is completed more than once",
        "Transaction variable '{0}' is completed more than once",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DotRocks SQL transactions and Stream Load transactions are single-use after commit or rollback."
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            InsecureStreamLoadEndpointRule,
            MissingValueGeneratedNeverRule,
            UnsupportedBinaryMappingRule,
            TransactionDoubleCompletionRule
        );

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeStringLiteral, SyntaxKind.StringLiteralExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeBlock, SyntaxKind.Block);
    }

    private static void AnalyzeStringLiteral(SyntaxNodeAnalysisContext context)
    {
        var literal = (LiteralExpressionSyntax)context.Node;
        string value = literal.Token.ValueText;
        if (Contains(value, "stream load endpoint=http://") && Contains(value, "password="))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(InsecureStreamLoadEndpointRule, literal.GetLocation())
            );
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (!IsMemberInvocation(invocation, "HasColumnType"))
        {
            return;
        }

        if (
            invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression
                is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.StringLiteralExpression)
        )
        {
            return;
        }

        string storeType = literal.Token.ValueText;
        if (
            string.Equals(storeType, "binary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storeType, "varbinary", StringComparison.OrdinalIgnoreCase)
            || storeType.StartsWith("binary(", StringComparison.OrdinalIgnoreCase)
            || storeType.StartsWith("varbinary(", StringComparison.OrdinalIgnoreCase)
        )
        {
            context.ReportDiagnostic(
                Diagnostic.Create(UnsupportedBinaryMappingRule, literal.GetLocation(), storeType)
            );
        }
    }

    private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (
            !string.Equals(method.Identifier.ValueText, "OnModelCreating", StringComparison.Ordinal)
            || method.Body is null
        )
        {
            return;
        }

        bool hasKey = method
            .Body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => IsMemberInvocation(invocation, "HasKey"));
        if (!hasKey)
        {
            return;
        }

        bool hasValueGeneratedNever = method
            .Body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => IsMemberInvocation(invocation, "ValueGeneratedNever"));
        if (!hasValueGeneratedNever)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(MissingValueGeneratedNeverRule, method.Identifier.GetLocation())
            );
        }
    }

    private static void AnalyzeBlock(SyntaxNodeAnalysisContext context)
    {
        var block = (BlockSyntax)context.Node;
        var completions = new Dictionary<string, Location>(StringComparer.Ordinal);
        foreach (
            InvocationExpressionSyntax invocation in block
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
        )
        {
            if (
                invocation.Expression is not MemberAccessExpressionSyntax memberAccess
                || memberAccess.Expression is not IdentifierNameSyntax receiver
                || !IsCompletionMethod(memberAccess.Name.Identifier.ValueText)
            )
            {
                continue;
            }

            string variableName = receiver.Identifier.ValueText;
            if (completions.ContainsKey(variableName))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        TransactionDoubleCompletionRule,
                        memberAccess.Name.GetLocation(),
                        variableName
                    )
                );
                continue;
            }

            completions.Add(variableName, memberAccess.Name.GetLocation());
        }
    }

    private static bool IsMemberInvocation(InvocationExpressionSyntax invocation, string name) =>
        invocation.Expression is MemberAccessExpressionSyntax memberAccess
        && string.Equals(memberAccess.Name.Identifier.ValueText, name, StringComparison.Ordinal);

    private static bool IsCompletionMethod(string methodName) =>
        string.Equals(methodName, "Commit", StringComparison.Ordinal)
        || string.Equals(methodName, "CommitAsync", StringComparison.Ordinal)
        || string.Equals(methodName, "Rollback", StringComparison.Ordinal)
        || string.Equals(methodName, "RollbackAsync", StringComparison.Ordinal);

    private static bool Contains(string text, string value) =>
        text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
}
