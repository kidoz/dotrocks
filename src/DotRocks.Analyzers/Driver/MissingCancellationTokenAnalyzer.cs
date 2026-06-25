using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.Driver;

/// <summary>
/// Reports asynchronous DotRocks calls that accept a <see cref="System.Threading.CancellationToken"/>
/// but do not pass the one available in the enclosing method.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingCancellationTokenAnalyzer : DiagnosticAnalyzer
{
    private const string CancellationTokenTypeName = "System.Threading.CancellationToken";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [DotRocksDiagnosticDescriptors.MissingCancellationToken];

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
        if (
            context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || !AnalyzerSyntaxHelpers.IsDotRocksSymbol(method)
            || !method.Name.EndsWith("Async", StringComparison.Ordinal)
        )
        {
            return;
        }

        // The called overload must accept a CancellationToken for the call to be fixable. Iterate
        // the ImmutableArray directly to avoid the delegate/enumerator allocations Any() incurs on
        // this per-invocation analyzer path.
        bool acceptsCancellationToken = false;
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (IsCancellationToken(parameter.Type))
            {
                acceptsCancellationToken = true;
                break;
            }
        }

        if (!acceptsCancellationToken)
        {
            return;
        }

        if (
            ArgumentsContainCancellationToken(context, invocation)
            || !IsCancellationTokenAvailableInScope(context, invocation)
        )
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                DotRocksDiagnosticDescriptors.MissingCancellationToken,
                invocation.GetLocation(),
                method.Name
            )
        );
    }

    private static bool ArgumentsContainCancellationToken(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation
    )
    {
        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
        {
            if (IsCancellationToken(context.SemanticModel.GetTypeInfo(argument.Expression).Type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCancellationTokenAvailableInScope(
        SyntaxNodeAnalysisContext context,
        SyntaxNode node
    )
    {
        foreach (SyntaxNode ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return HasCancellationTokenParameter(context, method.ParameterList);
                case LocalFunctionStatementSyntax localFunction:
                    if (HasCancellationTokenParameter(context, localFunction.ParameterList))
                    {
                        return true;
                    }

                    break;
                case ParenthesizedLambdaExpressionSyntax lambda:
                    if (HasCancellationTokenParameter(context, lambda.ParameterList))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static bool HasCancellationTokenParameter(
        SyntaxNodeAnalysisContext context,
        ParameterListSyntax? parameterList
    )
    {
        foreach (ParameterSyntax parameter in parameterList?.Parameters ?? [])
        {
            if (
                context.SemanticModel.GetDeclaredSymbol(parameter) is { Type: { } type }
                && IsCancellationToken(type)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCancellationToken(ITypeSymbol? type) =>
        AnalyzerSyntaxHelpers.IsNamedType(type, CancellationTokenTypeName);
}
