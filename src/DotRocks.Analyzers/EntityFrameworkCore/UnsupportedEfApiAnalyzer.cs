using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.EntityFrameworkCore;

/// <summary>
/// Reports EF Core APIs that DotRocks intentionally does not support.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnsupportedEfApiAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> UnsupportedDatabaseCreatorMethods =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "EnsureCreated",
            "EnsureCreatedAsync",
            "EnsureDeleted",
            "EnsureDeletedAsync"
        );

    private static readonly ImmutableHashSet<string> UnsupportedBulkDmlMethods =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "ExecuteUpdate",
            "ExecuteUpdateAsync",
            "ExecuteDelete",
            "ExecuteDeleteAsync"
        );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        DotRocksDiagnosticDescriptors.UnsupportedDatabaseCreator,
        DotRocksDiagnosticDescriptors.UnsupportedBulkDml,
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        string methodName = memberAccess.Name.Identifier.ValueText;
        if (UnsupportedDatabaseCreatorMethods.Contains(methodName))
        {
            ReportIfDatabaseCreatorApi(context, invocation, memberAccess, methodName);
            return;
        }

        if (UnsupportedBulkDmlMethods.Contains(methodName))
        {
            ReportIfBulkDmlApi(context, invocation, methodName);
        }
    }

    private static void ReportIfDatabaseCreatorApi(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        string methodName
    )
    {
        if (!IsEfDatabaseCreatorInvocation(context, invocation, memberAccess))
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                DotRocksDiagnosticDescriptors.UnsupportedDatabaseCreator,
                memberAccess.Name.GetLocation(),
                methodName
            )
        );
    }

    private static void ReportIfBulkDmlApi(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        string methodName
    )
    {
        if (!IsEfBulkDmlInvocation(context, invocation))
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                DotRocksDiagnosticDescriptors.UnsupportedBulkDml,
                invocation.GetLocation(),
                methodName
            )
        );
    }

    private static bool IsEfDatabaseCreatorInvocation(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess
    )
    {
        IMethodSymbol? method =
            context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (
            method?.ContainingType is not null
            && AnalyzerSyntaxHelpers.IsNamedType(
                method.ContainingType,
                "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade"
            )
        )
        {
            return true;
        }

        ITypeSymbol? receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
        return AnalyzerSyntaxHelpers.IsNamedType(
            receiverType,
            "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade"
        );
    }

    private static bool IsEfBulkDmlInvocation(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation
    )
    {
        IMethodSymbol? method =
            context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (method is null)
        {
            return false;
        }

        return IsEfNamespace(method.ContainingNamespace)
            || method.ReducedFrom is { ContainingNamespace: { } reducedNamespace }
                && IsEfNamespace(reducedNamespace);
    }

    private static bool IsEfNamespace(INamespaceSymbol? namespaceSymbol)
    {
        for (
            INamespaceSymbol? current = namespaceSymbol;
            current is { IsGlobalNamespace: false };
            current = current.ContainingNamespace
        )
        {
            if (
                string.Equals(current.Name, "EntityFrameworkCore", StringComparison.Ordinal)
                && current.ContainingNamespace is { Name: "Microsoft" }
            )
            {
                return true;
            }
        }

        return false;
    }
}
