using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.EntityFrameworkCore;

/// <summary>
/// Reports EF Core entities configured with a composite primary key, which DotRocks EF Core
/// rejects at model validation. Writable entities require a single-column primary key; read-only
/// entities should be mapped with HasNoKey().
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EfCompositePrimaryKeyAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [DotRocksDiagnosticDescriptors.CompositePrimaryKey];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.MethodDeclaration);
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

        foreach (
            InvocationExpressionSyntax hasKeyInvocation in method
                .Body.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation => AnalyzerSyntaxHelpers.IsMemberInvocation(invocation, "HasKey"))
        )
        {
            if (!IsCompositeKey(hasKeyInvocation))
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    DotRocksDiagnosticDescriptors.CompositePrimaryKey,
                    hasKeyInvocation.GetLocation(),
                    GetEntityTypeName(hasKeyInvocation) ?? "(unknown)"
                )
            );
        }
    }

    private static bool IsCompositeKey(InvocationExpressionSyntax hasKeyInvocation)
    {
        ExpressionSyntax? expression = hasKeyInvocation
            .ArgumentList.Arguments.FirstOrDefault()
            ?.Expression;

        expression = expression switch
        {
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.Body
                as ExpressionSyntax,
            SimpleLambdaExpressionSyntax simple => simple.Body as ExpressionSyntax,
            _ => expression,
        };

        // A composite key is expressed as `e => new { e.A, e.B }` with two or more members.
        return expression is AnonymousObjectCreationExpressionSyntax anonymousObject
            && anonymousObject.Initializers.Count > 1;
    }

    private static string? GetEntityTypeName(InvocationExpressionSyntax hasKeyInvocation)
    {
        // `modelBuilder.Entity<Widget>().HasKey(...)` — the generic Entity call is in the receiver.
        if (hasKeyInvocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            string? fromReceiver = FindEntityTypeArgument(memberAccess.Expression);
            if (fromReceiver is not null)
            {
                return fromReceiver;
            }
        }

        // `modelBuilder.Entity<Widget>(entity => entity.HasKey(...))` — walk out to the Entity call.
        foreach (
            InvocationExpressionSyntax ancestor in hasKeyInvocation
                .Ancestors()
                .OfType<InvocationExpressionSyntax>()
        )
        {
            string? fromAncestor = FindEntityTypeArgument(ancestor);
            if (fromAncestor is not null)
            {
                return fromAncestor;
            }
        }

        return null;
    }

    private static string? FindEntityTypeArgument(SyntaxNode node)
    {
        foreach (
            InvocationExpressionSyntax invocation in node.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
        )
        {
            if (
                invocation.Expression
                    is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
                && string.Equals(generic.Identifier.ValueText, "Entity", StringComparison.Ordinal)
                && generic.TypeArgumentList.Arguments.Count == 1
            )
            {
                return generic.TypeArgumentList.Arguments[0].ToString();
            }
        }

        return null;
    }
}
