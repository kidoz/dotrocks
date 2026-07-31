using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.Driver;

/// <summary>
/// Reports SQL built with string interpolation or concatenation that is assigned to a
/// DotRocks command's <c>CommandText</c> or passed to its constructor.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnsafeCommandTextAnalyzer : DiagnosticAnalyzer
{
    private const string DataCommandTypeName = "DotRocks.Data.DotRocksCommand";
    private const string FlightSqlCommandTypeName = "DotRocks.FlightSql.DotRocksFlightSqlCommand";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [DotRocksDiagnosticDescriptors.UnsafeCommandText];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
        context.RegisterSyntaxNodeAction(
            AnalyzeObjectCreation,
            SyntaxKind.ObjectCreationExpression
        );
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (
            assignment.Left is not MemberAccessExpressionSyntax memberAccess
            || !string.Equals(
                memberAccess.Name.Identifier.ValueText,
                "CommandText",
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        ITypeSymbol? receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
        if (IsCommandType(receiverType) && IsUnsafeSqlExpression(context, assignment.Right))
        {
            Report(context, assignment.Right);
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var objectCreation = (ObjectCreationExpressionSyntax)context.Node;
        if (!IsCommandType(context.SemanticModel.GetTypeInfo(objectCreation).Type))
        {
            return;
        }

        ArgumentSyntax? firstArgument = objectCreation.ArgumentList
            is { Arguments: { Count: > 0 } arguments }
            ? arguments[0]
            : null;
        if (firstArgument is not null && IsUnsafeSqlExpression(context, firstArgument.Expression))
        {
            Report(context, firstArgument.Expression);
        }
    }

    private static bool IsUnsafeSqlExpression(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        ExpressionSyntax unwrapped = Unwrap(expression);

        // A compile-time constant (literal or fully constant interpolation/concatenation) is safe.
        if (context.SemanticModel.GetConstantValue(unwrapped).HasValue)
        {
            return false;
        }

        return unwrapped switch
        {
            InterpolatedStringExpressionSyntax => true,
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) =>
                IsStringTyped(context, binary),
            _ => false,
        };
    }

    private static bool IsStringTyped(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        ITypeSymbol? type = context.SemanticModel.GetTypeInfo(expression).Type;
        return type?.SpecialType == SpecialType.System_String;
    }

    private static bool IsCommandType(ITypeSymbol? type) =>
        AnalyzerSyntaxHelpers.IsNamedType(type, DataCommandTypeName)
        || AnalyzerSyntaxHelpers.IsNamedType(type, FlightSqlCommandTypeName);

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) =>
        expression is ParenthesizedExpressionSyntax parenthesized
            ? Unwrap(parenthesized.Expression)
            : expression;

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node) =>
        context.ReportDiagnostic(
            Diagnostic.Create(DotRocksDiagnosticDescriptors.UnsafeCommandText, node.GetLocation())
        );
}
