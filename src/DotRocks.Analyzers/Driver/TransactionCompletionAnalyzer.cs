using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.Driver;

/// <summary>
/// Reports source-visible double completion of DotRocks transaction objects.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TransactionCompletionAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [DotRocksDiagnosticDescriptors.TransactionDoubleCompletion];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeBlock, SyntaxKind.Block);
    }

    private static void AnalyzeBlock(SyntaxNodeAnalysisContext context)
    {
        var block = (BlockSyntax)context.Node;
        var completions = new Dictionary<string, Location>(StringComparer.Ordinal);

        // Only consider completions that are top-level statements of *this* block. Completions
        // nested in if/else, switch, or try/catch branches each live in their own block, so
        // mutually-exclusive `Commit()`/`Rollback()` are not falsely paired. This intentionally
        // trades exhaustive flow analysis for zero false positives on the common idioms.
        foreach (StatementSyntax statement in block.Statements)
        {
            if (statement is not ExpressionStatementSyntax expressionStatement)
            {
                continue;
            }

            ExpressionSyntax expression = expressionStatement.Expression;
            if (expression is AwaitExpressionSyntax awaitExpression)
            {
                expression = awaitExpression.Expression;
            }

            if (
                expression is not InvocationExpressionSyntax invocation
                || invocation.Expression is not MemberAccessExpressionSyntax memberAccess
                || memberAccess.Expression is not IdentifierNameSyntax receiver
                || !IsCompletionMethod(memberAccess.Name.Identifier.ValueText)
                || !AnalyzerSyntaxHelpers.IsDotRocksTransactionType(
                    context.SemanticModel.GetTypeInfo(receiver).Type
                )
            )
            {
                continue;
            }

            string variableName = receiver.Identifier.ValueText;
            if (completions.ContainsKey(variableName))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DotRocksDiagnosticDescriptors.TransactionDoubleCompletion,
                        memberAccess.Name.GetLocation(),
                        variableName
                    )
                );
                continue;
            }

            completions.Add(variableName, memberAccess.Name.GetLocation());
        }
    }

    private static bool IsCompletionMethod(string methodName) =>
        string.Equals(methodName, "Commit", StringComparison.Ordinal)
        || string.Equals(methodName, "CommitAsync", StringComparison.Ordinal)
        || string.Equals(methodName, "Rollback", StringComparison.Ordinal)
        || string.Equals(methodName, "RollbackAsync", StringComparison.Ordinal);
}
