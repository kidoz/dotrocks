using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.EntityFrameworkCore;

/// <summary>
/// Reports unsupported EF Core binary and varbinary mappings.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnsupportedBinaryMappingAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [DotRocksDiagnosticDescriptors.UnsupportedBinaryMapping];

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
        if (!AnalyzerSyntaxHelpers.IsMemberInvocation(invocation, "HasColumnType"))
        {
            return;
        }

        if (
            invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } expression
            || AnalyzerSyntaxHelpers.GetConstantString(context, expression) is not { } storeType
        )
        {
            return;
        }

        if (
            string.Equals(storeType, "binary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storeType, "varbinary", StringComparison.OrdinalIgnoreCase)
            || storeType.StartsWith("binary(", StringComparison.OrdinalIgnoreCase)
            || storeType.StartsWith("varbinary(", StringComparison.OrdinalIgnoreCase)
        )
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    DotRocksDiagnosticDescriptors.UnsupportedBinaryMapping,
                    expression.GetLocation(),
                    storeType
                )
            );
        }
    }
}
