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

    public static bool IsNamedType(ITypeSymbol? type, string metadataName) =>
        string.Equals(
            type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "global::" + metadataName,
            StringComparison.Ordinal
        );

    public static bool IsDotRocksTransactionType(ITypeSymbol? type) =>
        IsNamedType(type, "DotRocks.Data.DotRocksTransaction")
        || IsNamedType(type, "DotRocks.Data.Loading.DotRocksStreamLoadTransaction");
}
