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
/// Code fix provider for simple EF Core key ValueGeneratedNever() configuration.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EfValueGeneratedNeverCodeFixProvider))]
[Shared]
public sealed class EfValueGeneratedNeverCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
    [DotRocksDiagnosticDescriptors.MissingValueGeneratedNeverDiagnosticId];

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
            InvocationExpressionSyntax? hasKeyInvocation = root.FindNode(
                    diagnostic.Location.SourceSpan,
                    getInnermostNodeForTie: true
                )
                .FirstAncestorOrSelf<InvocationExpressionSyntax>(IsHasKeyInvocation);
            if (
                hasKeyInvocation is null
                || TryCreateValueGeneratedNeverStatement(hasKeyInvocation, diagnostic)
                    is not { } statement
            )
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Configure ValueGeneratedNever()",
                    cancellationToken =>
                        InsertStatementAsync(
                            document,
                            hasKeyInvocation,
                            statement,
                            cancellationToken
                        ),
                    nameof(EfValueGeneratedNeverCodeFixProvider)
                ),
                diagnostic
            );
        }
    }

    private static async Task<Document> InsertStatementAsync(
        Document document,
        InvocationExpressionSyntax hasKeyInvocation,
        StatementSyntax statement,
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

        StatementSyntax? sourceStatement = hasKeyInvocation.FirstAncestorOrSelf<StatementSyntax>();
        if (sourceStatement is null)
        {
            return document;
        }

        StatementSyntax replacementStatement = statement
            .WithLeadingTrivia(sourceStatement.GetLeadingTrivia())
            .WithTrailingTrivia(sourceStatement.GetTrailingTrivia());
        SyntaxNode newRoot = root.InsertNodesAfter(sourceStatement, [replacementStatement]);
        return document.WithSyntaxRoot(newRoot);
    }

    private static StatementSyntax? TryCreateValueGeneratedNeverStatement(
        InvocationExpressionSyntax hasKeyInvocation,
        Diagnostic diagnostic
    )
    {
        if (
            hasKeyInvocation.Expression is not MemberAccessExpressionSyntax hasKeyMember
            || hasKeyMember.Expression is not { } entityExpression
            || hasKeyInvocation.ArgumentList.Arguments.FirstOrDefault()?.Expression
                is not SimpleLambdaExpressionSyntax lambda
            || lambda.Body is not MemberAccessExpressionSyntax memberAccess
            || !diagnostic.Location.SourceSpan.IntersectsWith(memberAccess.Name.Span)
            || hasKeyInvocation.FirstAncestorOrSelf<StatementSyntax>() is not { } sourceStatement
            || sourceStatement.Parent is not BlockSyntax
            || !IsReceiverInScope(entityExpression, sourceStatement)
        )
        {
            return null;
        }

        string parameterName = lambda.Parameter.Identifier.ValueText;
        string propertyName = memberAccess.Name.Identifier.ValueText;
        return SyntaxFactory.ParseStatement(
            $"{entityExpression}.Property({parameterName} => {parameterName}.{propertyName}).ValueGeneratedNever();"
        );
    }

    // The inserted statement is a sibling of the statement that contains HasKey. An expression-bodied
    // lambda such as Entity(w => w.HasKey(...)) ends before that sibling, so w would not compile.
    private static bool IsReceiverInScope(
        ExpressionSyntax entityExpression,
        StatementSyntax sourceStatement
    )
    {
        foreach (
            IdentifierNameSyntax identifier in entityExpression
                .DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
        )
        {
            if (!IsIdentifierInScope(identifier, sourceStatement))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierInScope(
        IdentifierNameSyntax identifier,
        StatementSyntax sourceStatement
    )
    {
        string name = identifier.Identifier.ValueText;
        for (SyntaxNode? node = identifier.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case SimpleLambdaExpressionSyntax simpleLambda
                    when simpleLambda.Parameter.Identifier.ValueText == name:
                    return simpleLambda.Body is BlockSyntax simpleBody
                        && simpleBody.Contains(sourceStatement);
                case ParenthesizedLambdaExpressionSyntax parenthesizedLambda
                    when DeclaresParameter(parenthesizedLambda.ParameterList, name):
                    return parenthesizedLambda.Body is BlockSyntax parenthesizedBody
                        && parenthesizedBody.Contains(sourceStatement);
                case LocalFunctionStatementSyntax localFunction
                    when DeclaresParameter(localFunction.ParameterList, name):
                    return localFunction.Body?.Contains(sourceStatement) == true;
                case BaseMethodDeclarationSyntax method
                    when DeclaresParameter(method.ParameterList, name):
                    return method.Body?.Contains(sourceStatement) == true;
                case BlockSyntax localBlock
                    when DeclaresLocalBefore(localBlock, name, sourceStatement):
                    return true;
            }
        }

        // A name with no local or parameter declaration is a type or member, which stays in scope.
        return true;
    }

    private static bool DeclaresParameter(BaseParameterListSyntax parameterList, string name) =>
        parameterList.Parameters.Any(parameter => parameter.Identifier.ValueText == name);

    private static bool DeclaresLocalBefore(
        BlockSyntax block,
        string name,
        StatementSyntax sourceStatement
    )
    {
        foreach (StatementSyntax statement in block.Statements)
        {
            if (statement.SpanStart >= sourceStatement.SpanStart)
            {
                return false;
            }

            if (
                statement is LocalDeclarationStatementSyntax local
                && local.Declaration.Variables.Any(variable =>
                    variable.Identifier.ValueText == name
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHasKeyInvocation(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax memberAccess
        && string.Equals(
            memberAccess.Name.Identifier.ValueText,
            "HasKey",
            StringComparison.Ordinal
        );
}
