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
    [DotRocksDiagnosticDescriptors.MultiRowSaveChanges];

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

            ISymbol? rangeContext = GetContextSymbol(context, invocation);
            if (!HasLaterSaveChangesInvocation(context, invocations, index + 1, rangeContext))
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
        int startIndex,
        ISymbol? rangeContext
    )
    {
        for (int index = startIndex; index < invocations.Length; index++)
        {
            InvocationExpressionSyntax invocation = invocations[index];
            if (
                !TryGetMemberInvocationName(invocation, out string? methodName)
                || !SaveChangesMethods.Contains(methodName)
                || !IsEfSaveChangesInvocation(context, invocation)
            )
            {
                continue;
            }

            // Only pair a range change with a SaveChanges on the *same* DbContext. When both
            // receivers resolve to a symbol, require them to match; otherwise fall back to the
            // positional pairing so an unresolved receiver does not silence a real warning.
            ISymbol? saveContext = GetContextSymbol(context, invocation);
            if (
                rangeContext is not null
                && saveContext is not null
                && !SymbolEqualityComparer.Default.Equals(rangeContext, saveContext)
            )
            {
                continue;
            }

            // Skip pairs that live in mutually-exclusive branches (different arms of the same
            // if/else or switch); they never run in one unit of work.
            if (AreInMutuallyExclusiveBranches(invocations[startIndex - 1], invocation))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    // Returns the DbContext instance symbol behind a range-change or SaveChanges invocation, whether
    // it is called directly on the context (context.AddRange/context.SaveChanges) or on one of its
    // sets (context.Widgets.AddRange). Returns null when it cannot be resolved.
    private static ISymbol? GetContextSymbol(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation
    )
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        ExpressionSyntax receiver = memberAccess.Expression;
        ITypeSymbol? receiverType = context.SemanticModel.GetTypeInfo(receiver).Type;

        // A range change on a DbSet: the owning context is the expression before the set access.
        if (
            IsEfDbSet(receiverType)
            && receiver is MemberAccessExpressionSyntax setAccess
            && IsDbContextTyped(context, setAccess.Expression)
        )
        {
            return context.SemanticModel.GetSymbolInfo(setAccess.Expression).Symbol;
        }

        return IsDbContextTyped(context, receiver)
            ? context.SemanticModel.GetSymbolInfo(receiver).Symbol
            : null;
    }

    private static bool IsDbContextTyped(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    ) => IsEfDbContext(context.SemanticModel.GetTypeInfo(expression).Type);

    // True when the two nodes sit in different arms of the same if/else or switch, so at most one
    // of them executes on any path.
    private static bool AreInMutuallyExclusiveBranches(SyntaxNode first, SyntaxNode second)
    {
        foreach (SyntaxNode ancestor in first.Ancestors())
        {
            switch (ancestor)
            {
                case IfStatementSyntax ifStatement when ifStatement.Else is { } elseClause:
                    if (ifStatement.Statement.Contains(first) && elseClause.Contains(second))
                    {
                        return true;
                    }

                    if (elseClause.Contains(first) && ifStatement.Statement.Contains(second))
                    {
                        return true;
                    }

                    break;
                case SwitchStatementSyntax switchStatement:
                    SwitchSectionSyntax? firstSection = FindSection(switchStatement, first);
                    SwitchSectionSyntax? secondSection = FindSection(switchStatement, second);
                    if (
                        firstSection is not null
                        && secondSection is not null
                        && firstSection != secondSection
                    )
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static SwitchSectionSyntax? FindSection(
        SwitchStatementSyntax switchStatement,
        SyntaxNode node
    )
    {
        foreach (SwitchSectionSyntax section in switchStatement.Sections)
        {
            if (section.Contains(node))
            {
                return section;
            }
        }

        return null;
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

    private static bool IsEfDbSet(ITypeSymbol? type) =>
        AnalyzerSyntaxHelpers.IsNamedType(type, "Microsoft.EntityFrameworkCore.DbSet", arity: 1);

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
