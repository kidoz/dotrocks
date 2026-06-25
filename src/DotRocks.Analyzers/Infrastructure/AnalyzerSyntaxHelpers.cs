using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.Infrastructure;

internal static class AnalyzerSyntaxHelpers
{
    public static bool IsMemberInvocation(InvocationExpressionSyntax invocation, string name) =>
        invocation.Expression is MemberAccessExpressionSyntax memberAccess
        && string.Equals(memberAccess.Name.Identifier.ValueText, name, StringComparison.Ordinal);

    public static string? GetConstantString(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        Optional<object?> value = context.SemanticModel.GetConstantValue(expression);
        return value.HasValue ? value.Value as string : null;
    }

    public static bool IsNamedType(ITypeSymbol? type, string metadataName)
    {
        if (type is null)
        {
            return false;
        }

        // Walk the type name and its containing namespaces from the right, comparing each dotted
        // segment of the metadata name. This avoids allocating the fully-qualified display string
        // that ToDisplayString would build on every per-node call.
        ReadOnlySpan<char> remaining = metadataName.AsSpan();
        if (!MatchTrailingSegment(ref remaining, type.Name))
        {
            return false;
        }

        INamespaceSymbol? containingNamespace = type.ContainingNamespace;
        while (!remaining.IsEmpty)
        {
            if (
                containingNamespace is null or { IsGlobalNamespace: true }
                || !MatchTrailingSegment(ref remaining, containingNamespace.Name)
            )
            {
                return false;
            }

            containingNamespace = containingNamespace.ContainingNamespace;
        }

        // All metadata segments matched; the type must bottom out at the global namespace.
        return containingNamespace is null or { IsGlobalNamespace: true };
    }

    // Compares the last dotted segment of <paramref name="dotted"/> to <paramref name="segment"/>
    // and, on a match, trims that segment (and its separator) so the caller can match the next one.
    private static bool MatchTrailingSegment(ref ReadOnlySpan<char> dotted, string segment)
    {
        int lastDot = dotted.LastIndexOf('.');
        if (!dotted.Slice(lastDot + 1).SequenceEqual(segment.AsSpan()))
        {
            return false;
        }

        dotted = lastDot < 0 ? ReadOnlySpan<char>.Empty : dotted.Slice(0, lastDot);
        return true;
    }

    public static bool IsDotRocksTransactionType(ITypeSymbol? type) =>
        IsNamedType(type, "DotRocks.Data.DotRocksTransaction")
        || IsNamedType(type, "DotRocks.Data.Loading.DotRocksStreamLoadTransaction");

    /// <summary>
    /// Returns true when the symbol belongs to the DotRocks product namespaces. Used to gate
    /// analyzers so they do not fire on unrelated user code that merely contains similar strings.
    /// </summary>
    public static bool IsDotRocksSymbol(ISymbol? symbol)
    {
        for (
            INamespaceSymbol? ns =
                symbol?.ContainingType?.ContainingNamespace ?? symbol?.ContainingNamespace;
            ns is { IsGlobalNamespace: false };
            ns = ns.ContainingNamespace
        )
        {
            if (
                string.Equals(ns.Name, "DotRocks", StringComparison.Ordinal)
                && ns.ContainingNamespace is { IsGlobalNamespace: true }
            )
            {
                return true;
            }
        }

        return false;
    }
}
