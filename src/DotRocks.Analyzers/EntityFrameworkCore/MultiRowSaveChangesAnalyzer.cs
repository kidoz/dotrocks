using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.EntityFrameworkCore;

/// <summary>
/// Reports source-visible range changes that are saved as one EF Core SaveChanges operation.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MultiRowSaveChangesAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> RangeChangeMethods = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "AddRange",
        "UpdateRange",
        "RemoveRange"
    );

    private static readonly ImmutableHashSet<string> SaveChangesMethods = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "SaveChanges",
        "SaveChangesAsync"
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DotRocksDiagnosticDescriptors.MultiRowSaveChanges);

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
        InvocationExpressionSyntax[] invocations = block
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToArray();
        for (int index = 0; index < invocations.Length; index++)
        {
            InvocationExpressionSyntax invocation = invocations[index];
            if (!TryGetMemberInvocationName(invocation, out string? rangeMethodName))
            {
                continue;
            }

            if (!RangeChangeMethods.Contains(rangeMethodName))
            {
                continue;
            }

            if (!IsEfRangeChangeInvocation(context, invocation))
            {
                continue;
            }

            if (!HasLaterSaveChangesInvocation(context, invocations, index + 1))
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    DotRocksDiagnosticDescriptors.MultiRowSaveChanges,
                    invocation.GetLocation(),
                    rangeMethodName
                )
            );
        }
    }

    private static bool HasLaterSaveChangesInvocation(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax[] invocations,
        int startIndex
    )
    {
        for (int index = startIndex; index < invocations.Length; index++)
        {
            InvocationExpressionSyntax invocation = invocations[index];
            if (
                TryGetMemberInvocationName(invocation, out string? methodName)
                && SaveChangesMethods.Contains(methodName)
                && IsEfSaveChangesInvocation(context, invocation)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEfRangeChangeInvocation(
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

        return IsEfDbSet(method.ContainingType) || IsEfDbContext(method.ContainingType);
    }

    private static bool IsEfSaveChangesInvocation(
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

        return IsEfDbContext(method.ContainingType);
    }

    private static bool TryGetMemberInvocationName(
        InvocationExpressionSyntax invocation,
        out string methodName
    )
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            methodName = memberAccess.Name.Identifier.ValueText;
            return true;
        }

        methodName = string.Empty;
        return false;
    }

    private static bool IsEfDbSet(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        return string.Equals(
            namedType.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "global::Microsoft.EntityFrameworkCore.DbSet<TEntity>",
            StringComparison.Ordinal
        );
    }

    private static bool IsEfDbContext(ITypeSymbol? type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (
                AnalyzerSyntaxHelpers.IsNamedType(
                    current,
                    "Microsoft.EntityFrameworkCore.DbContext"
                )
            )
            {
                return true;
            }
        }

        return false;
    }
}
