using System.Collections.Immutable;
using System.Composition;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotRocks.Analyzers.CodeFixes;

/// <summary>
/// Code fix provider for insecure Stream Load endpoint literals.
/// </summary>
[ExportCodeFixProvider(
    LanguageNames.CSharp,
    Name = nameof(InsecureStreamLoadEndpointCodeFixProvider)
)]
[Shared]
public sealed class InsecureStreamLoadEndpointCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpointDiagnosticId);

    /// <inheritdoc />
    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Document document = context.Document;
        SyntaxNode? root = await document
            .GetSyntaxRootAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            LiteralExpressionSyntax? literal = root.FindNode(
                    diagnostic.Location.SourceSpan,
                    getInnermostNodeForTie: true
                )
                .FirstAncestorOrSelf<LiteralExpressionSyntax>();
            if (literal is null || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                continue;
            }

            string value = literal.Token.ValueText;
            if (!value.Contains("http://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Use HTTPS Stream Load endpoint",
                    cancellationToken =>
                        ReplaceLiteralAsync(document, literal, value, cancellationToken),
                    nameof(InsecureStreamLoadEndpointCodeFixProvider)
                ),
                diagnostic
            );
        }
    }

    private static async Task<Document> ReplaceLiteralAsync(
        Document document,
        LiteralExpressionSyntax literal,
        string value,
        CancellationToken cancellationToken
    )
    {
        SyntaxNode? root = await document
            .GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        string updated = ReplaceFirstHttpScheme(value);
        LiteralExpressionSyntax replacement = SyntaxFactory
            .LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(updated))
            .WithTriviaFrom(literal);
        return document.WithSyntaxRoot(root.ReplaceNode(literal, replacement));
    }

    private static string ReplaceFirstHttpScheme(string value)
    {
        int index = value.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? value : value.Remove(index, "http://".Length).Insert(index, "https://");
    }
}
