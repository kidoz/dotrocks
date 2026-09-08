using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.EntityFrameworkCore;

/// <summary>
/// Reports EF Core APIs that DotRocks intentionally does not support. Formerly also reported
/// DTR0006 for <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>; that rule was retired when DotRocks
/// EF Core gained single-table <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> translation.
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

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [DotRocksDiagnosticDescriptors.UnsupportedDatabaseCreator];

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
        if (
            !UnsupportedDatabaseCreatorMethods.Contains(methodName)
            || !IsEfDatabaseCreatorInvocation(context, invocation, memberAccess)
        )
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
}
