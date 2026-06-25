using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.Driver;

/// <summary>
/// Reports a hard-coded password embedded in a DotRocks connection string in source.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LiteralPasswordAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] PasswordKeywords = ["password=", "pwd="];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DotRocksDiagnosticDescriptors.LiteralPassword);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeObjectCreation,
            SyntaxKind.ObjectCreationExpression
        );
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeBlock, SyntaxKind.Block);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var objectCreation = (ObjectCreationExpressionSyntax)context.Node;
        if (!IsConnectionStringConsumer(context.SemanticModel.GetTypeInfo(objectCreation).Type))
        {
            return;
        }

        foreach (ArgumentSyntax argument in objectCreation.ArgumentList?.Arguments ?? [])
        {
            ReportIfConstantPassword(context, argument.Expression);
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Only inspect calls into DotRocks APIs to avoid flagging unrelated user code that merely
        // contains a similar string (for example a logging call).
        if (
            !AnalyzerSyntaxHelpers.IsDotRocksSymbol(
                context.SemanticModel.GetSymbolInfo(invocation).Symbol
            )
        )
        {
            return;
        }

        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
        {
            ReportIfConstantPassword(context, argument.Expression);
        }
    }

    private static void AnalyzeBlock(SyntaxNodeAnalysisContext context)
    {
        var block = (BlockSyntax)context.Node;
        Dictionary<string, ExpressionSyntax> localPasswordStrings = CollectLocalPasswordStrings(
            context,
            block
        );
        if (localPasswordStrings.Count == 0)
        {
            return;
        }

        foreach (
            ObjectCreationExpressionSyntax objectCreation in block
                .DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
        )
        {
            if (!IsConnectionStringConsumer(context.SemanticModel.GetTypeInfo(objectCreation).Type))
            {
                continue;
            }

            foreach (ArgumentSyntax argument in objectCreation.ArgumentList?.Arguments ?? [])
            {
                ReportIfLocalPassword(context, localPasswordStrings, argument.Expression);
            }
        }

        foreach (
            InvocationExpressionSyntax invocation in block
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
        )
        {
            if (
                !AnalyzerSyntaxHelpers.IsDotRocksSymbol(
                    context.SemanticModel.GetSymbolInfo(invocation).Symbol
                )
            )
            {
                continue;
            }

            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                ReportIfLocalPassword(context, localPasswordStrings, argument.Expression);
            }
        }
    }

    private static Dictionary<string, ExpressionSyntax> CollectLocalPasswordStrings(
        SyntaxNodeAnalysisContext context,
        BlockSyntax block
    )
    {
        var values = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        foreach (
            VariableDeclaratorSyntax variable in block
                .DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
        )
        {
            if (
                variable.Initializer?.Value is { } valueExpression
                && AnalyzerSyntaxHelpers.GetConstantString(context, valueExpression) is { } value
                && ContainsLiteralPassword(value)
            )
            {
                values[variable.Identifier.ValueText] = valueExpression;
            }
        }

        return values;
    }

    private static void ReportIfLocalPassword(
        SyntaxNodeAnalysisContext context,
        Dictionary<string, ExpressionSyntax> localPasswordStrings,
        ExpressionSyntax expression
    )
    {
        if (
            expression is IdentifierNameSyntax identifier
            && localPasswordStrings.ContainsKey(identifier.Identifier.ValueText)
        )
        {
            Report(context, expression);
        }
    }

    private static void ReportIfConstantPassword(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        if (
            AnalyzerSyntaxHelpers.GetConstantString(context, expression) is { } connectionString
            && ContainsLiteralPassword(connectionString)
        )
        {
            Report(context, expression);
        }
    }

    private static bool ContainsLiteralPassword(string connectionString)
    {
        foreach (string keyword in PasswordKeywords)
        {
            int keyIndex = connectionString.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
            {
                continue;
            }

            int valueStart = keyIndex + keyword.Length;
            int valueEnd = connectionString.IndexOf(';', valueStart);
            string value =
                valueEnd < 0
                    ? connectionString.Substring(valueStart)
                    : connectionString.Substring(valueStart, valueEnd - valueStart);
            if (value.Trim().Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsConnectionStringConsumer(ITypeSymbol? type) =>
        AnalyzerSyntaxHelpers.IsNamedType(type, "DotRocks.Data.DotRocksConnection")
        || AnalyzerSyntaxHelpers.IsNamedType(type, "DotRocks.Data.DotRocksDataSource")
        || AnalyzerSyntaxHelpers.IsNamedType(type, "DotRocks.Data.DotRocksConnectionStringBuilder")
        || AnalyzerSyntaxHelpers.IsNamedType(
            type,
            "DotRocks.Data.Loading.DotRocksStreamLoadClient"
        );

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node) =>
        context.ReportDiagnostic(
            Diagnostic.Create(DotRocksDiagnosticDescriptors.LiteralPassword, node.GetLocation())
        );
}
