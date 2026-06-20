using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.Driver;

/// <summary>
/// Reports source-visible HTTP Stream Load endpoints used with credentials.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InsecureStreamLoadEndpointAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpoint);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeObjectCreation,
            SyntaxKind.ObjectCreationExpression
        );
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeBlock, SyntaxKind.Block);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var objectCreation = (ObjectCreationExpressionSyntax)context.Node;
        ITypeSymbol? type = context.SemanticModel.GetTypeInfo(objectCreation).Type;

        if (IsDotRocksConnectionStringConsumer(type))
        {
            foreach (ArgumentSyntax argument in objectCreation.ArgumentList?.Arguments ?? [])
            {
                ReportIfInsecureConnectionString(context, argument.Expression);
            }
        }

        if (
            AnalyzerSyntaxHelpers.IsNamedType(type, "DotRocks.Data.DotRocksConnectionStringBuilder")
            && objectCreation.Initializer is not null
        )
        {
            ReportIfInsecureConnectionStringBuilderInitializer(context, objectCreation.Initializer);
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Only inspect calls into DotRocks APIs. Without this gate the analyzer would flag any
        // method anywhere (e.g. a logging call) whose argument happens to contain a connection
        // string, producing false positives that break consumer builds under warnings-as-errors.
        ISymbol? target = context.SemanticModel.GetSymbolInfo(invocation).Symbol;
        if (!AnalyzerSyntaxHelpers.IsDotRocksSymbol(target))
        {
            return;
        }

        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
        {
            ReportIfInsecureConnectionString(context, argument.Expression);
        }
    }

    private static void AnalyzeBlock(SyntaxNodeAnalysisContext context)
    {
        var block = (BlockSyntax)context.Node;
        Dictionary<string, string> localConnectionStrings = CollectLocalStringValues(
            context,
            block
        );
        Dictionary<string, BuilderState> localBuilders = CollectLocalBuilderStates(context, block);

        foreach (
            InvocationExpressionSyntax invocation in block
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
        )
        {
            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                ReportIfInsecureLocalConnectionString(
                    context,
                    localConnectionStrings,
                    argument.Expression
                );
                ReportIfInsecureBuilderConnectionString(
                    context,
                    localBuilders,
                    argument.Expression
                );
            }
        }

        foreach (
            ObjectCreationExpressionSyntax objectCreation in block
                .DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
        )
        {
            if (
                !IsDotRocksConnectionStringConsumer(
                    context.SemanticModel.GetTypeInfo(objectCreation).Type
                )
            )
            {
                continue;
            }

            foreach (ArgumentSyntax argument in objectCreation.ArgumentList?.Arguments ?? [])
            {
                ReportIfInsecureLocalConnectionString(
                    context,
                    localConnectionStrings,
                    argument.Expression
                );
                ReportIfInsecureBuilderConnectionString(
                    context,
                    localBuilders,
                    argument.Expression
                );
            }
        }
    }

    private static Dictionary<string, string> CollectLocalStringValues(
        SyntaxNodeAnalysisContext context,
        BlockSyntax block
    )
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (
            VariableDeclaratorSyntax variable in block
                .DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
        )
        {
            if (
                variable.Initializer?.Value is { } valueExpression
                && AnalyzerSyntaxHelpers.GetConstantString(context, valueExpression) is { } value
            )
            {
                values[variable.Identifier.ValueText] = value;
            }
        }

        return values;
    }

    private static Dictionary<string, BuilderState> CollectLocalBuilderStates(
        SyntaxNodeAnalysisContext context,
        BlockSyntax block
    )
    {
        var builders = new Dictionary<string, BuilderState>(StringComparer.Ordinal);
        foreach (
            VariableDeclaratorSyntax variable in block
                .DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
        )
        {
            if (
                variable.Initializer?.Value is ObjectCreationExpressionSyntax objectCreation
                && AnalyzerSyntaxHelpers.IsNamedType(
                    context.SemanticModel.GetTypeInfo(objectCreation).Type,
                    "DotRocks.Data.DotRocksConnectionStringBuilder"
                )
                && objectCreation.Initializer is not null
            )
            {
                BuilderState state = GetBuilderState(context, objectCreation.Initializer);
                builders[variable.Identifier.ValueText] = state;
            }
        }

        return builders;
    }

    private static void ReportIfInsecureLocalConnectionString(
        SyntaxNodeAnalysisContext context,
        Dictionary<string, string> localConnectionStrings,
        ExpressionSyntax expression
    )
    {
        if (
            expression is IdentifierNameSyntax identifier
            && localConnectionStrings.TryGetValue(
                identifier.Identifier.ValueText,
                out string? value
            )
            && IsInsecureStreamLoadConnectionString(value)
        )
        {
            Report(context, expression);
        }
    }

    private static void ReportIfInsecureBuilderConnectionString(
        SyntaxNodeAnalysisContext context,
        Dictionary<string, BuilderState> localBuilders,
        ExpressionSyntax expression
    )
    {
        if (
            expression is MemberAccessExpressionSyntax memberAccess
            && string.Equals(
                memberAccess.Name.Identifier.ValueText,
                "ConnectionString",
                StringComparison.Ordinal
            )
            && memberAccess.Expression is IdentifierNameSyntax builderIdentifier
            && localBuilders.TryGetValue(
                builderIdentifier.Identifier.ValueText,
                out BuilderState state
            )
            && state.IsInsecure
        )
        {
            Report(context, expression);
        }
    }

    private static void ReportIfInsecureConnectionString(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        if (
            AnalyzerSyntaxHelpers.GetConstantString(context, expression) is { } connectionString
            && IsInsecureStreamLoadConnectionString(connectionString)
        )
        {
            Report(context, expression);
        }
    }

    private static void ReportIfInsecureConnectionStringBuilderInitializer(
        SyntaxNodeAnalysisContext context,
        InitializerExpressionSyntax initializer
    )
    {
        BuilderState state = GetBuilderState(context, initializer);
        if (state.EndpointExpression is not null && state.IsInsecure)
        {
            Report(context, state.EndpointExpression);
        }
    }

    private static BuilderState GetBuilderState(
        SyntaxNodeAnalysisContext context,
        InitializerExpressionSyntax initializer
    )
    {
        ExpressionSyntax? httpEndpointExpression = null;
        bool hasPassword = false;

        foreach (ExpressionSyntax expression in initializer.Expressions)
        {
            if (
                expression is AssignmentExpressionSyntax assignment
                && assignment.Left is IdentifierNameSyntax identifier
            )
            {
                if (
                    string.Equals(
                        identifier.Identifier.ValueText,
                        "StreamLoadEndpoint",
                        StringComparison.Ordinal
                    )
                    && AnalyzerSyntaxHelpers.GetConstantString(context, assignment.Right)
                        is { } endpoint
                    && endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                )
                {
                    httpEndpointExpression = assignment.Right;
                }

                if (
                    string.Equals(
                        identifier.Identifier.ValueText,
                        "Password",
                        StringComparison.Ordinal
                    )
                    && !string.IsNullOrEmpty(
                        AnalyzerSyntaxHelpers.GetConstantString(context, assignment.Right)
                    )
                )
                {
                    hasPassword = true;
                }
            }
        }

        return new BuilderState(httpEndpointExpression, hasPassword);
    }

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node)
    {
        context.ReportDiagnostic(
            Diagnostic.Create(
                DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpoint,
                node.GetLocation()
            )
        );
    }

    private static bool IsInsecureStreamLoadConnectionString(string value) =>
        value.IndexOf("stream load endpoint=http://", StringComparison.OrdinalIgnoreCase) >= 0
        && value.IndexOf("password=", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsDotRocksConnectionStringConsumer(ITypeSymbol? type) =>
        AnalyzerSyntaxHelpers.IsNamedType(type, "DotRocks.Data.DotRocksConnection")
        || AnalyzerSyntaxHelpers.IsNamedType(type, "DotRocks.Data.DotRocksDataSource")
        || AnalyzerSyntaxHelpers.IsNamedType(type, "DotRocks.Data.DotRocksConnectionStringBuilder")
        || AnalyzerSyntaxHelpers.IsNamedType(
            type,
            "DotRocks.Data.Loading.DotRocksStreamLoadClient"
        );

    private readonly struct BuilderState(ExpressionSyntax? endpointExpression, bool hasPassword)
    {
        public ExpressionSyntax? EndpointExpression { get; } = endpointExpression;

        public bool IsInsecure => EndpointExpression is not null && hasPassword;
    }
}
