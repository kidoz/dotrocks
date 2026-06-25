using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.Driver;

/// <summary>
/// Reports blocking on an asynchronous DotRocks operation through <c>.Result</c>, <c>.Wait()</c>,
/// or <c>.GetAwaiter().GetResult()</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SyncOverAsyncAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [DotRocksDiagnosticDescriptors.SyncOverAsync];

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
            AnalyzeMemberAccess,
            SyntaxKind.SimpleMemberAccessExpression
        );
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;

        // Task<T>.Result blocks the caller. .GetResult() is handled via its invocation.
        if (
            string.Equals(
                memberAccess.Name.Identifier.ValueText,
                "Result",
                StringComparison.Ordinal
            ) && TryGetDotRocksAsyncCallName(context, memberAccess.Expression) is { } methodName
        )
        {
            Report(context, memberAccess, methodName, ".Result");
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        string memberName = memberAccess.Name.Identifier.ValueText;

        // task.Wait()
        if (
            string.Equals(memberName, "Wait", StringComparison.Ordinal)
            && TryGetDotRocksAsyncCallName(context, memberAccess.Expression) is { } waitTarget
        )
        {
            Report(context, invocation, waitTarget, ".Wait()");
            return;
        }

        // task.GetAwaiter().GetResult()
        if (
            string.Equals(memberName, "GetResult", StringComparison.Ordinal)
            && memberAccess.Expression is InvocationExpressionSyntax awaiterInvocation
            && awaiterInvocation.Expression is MemberAccessExpressionSyntax awaiterAccess
            && string.Equals(
                awaiterAccess.Name.Identifier.ValueText,
                "GetAwaiter",
                StringComparison.Ordinal
            )
            && TryGetDotRocksAsyncCallName(context, awaiterAccess.Expression) is { } awaiterTarget
        )
        {
            Report(context, invocation, awaiterTarget, ".GetAwaiter().GetResult()");
        }
    }

    private static string? TryGetDotRocksAsyncCallName(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        ExpressionSyntax unwrapped = expression is ParenthesizedExpressionSyntax parenthesized
            ? parenthesized.Expression
            : expression;

        if (
            unwrapped is InvocationExpressionSyntax invocation
            && context.SemanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
            && AnalyzerSyntaxHelpers.IsDotRocksSymbol(method)
            && method.Name.EndsWith("Async", StringComparison.Ordinal)
        )
        {
            return method.Name;
        }

        return null;
    }

    private static void Report(
        SyntaxNodeAnalysisContext context,
        SyntaxNode node,
        string methodName,
        string blockingMember
    ) =>
        context.ReportDiagnostic(
            Diagnostic.Create(
                DotRocksDiagnosticDescriptors.SyncOverAsync,
                node.GetLocation(),
                methodName,
                blockingMember
            )
        );
}
