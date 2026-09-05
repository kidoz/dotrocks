using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

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
        context.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment);
        context.RegisterOperationAction(AnalyzeObjectCreation, OperationKind.ObjectCreation);
        context.RegisterSyntaxNodeAction(
            AnalyzeUnboundObjectCreation,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression
        );
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (
            assignment.Target is not IPropertyReferenceOperation propertyReference
            || !string.Equals(
                propertyReference.Property.Name,
                "CommandText",
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        // Operations cover object initializers and C# 14 null-conditional assignments as well
        // as ordinary member access, without depending on each syntax shape.
        if (
            IsCommandType(propertyReference.Property.ContainingType)
            && IsUnsafeSqlExpression(assignment.Value)
        )
        {
            Report(context, assignment.Value.Syntax);
        }
    }

    private static void AnalyzeObjectCreation(OperationAnalysisContext context)
    {
        var objectCreation = (IObjectCreationOperation)context.Operation;
        if (!IsCommandType(objectCreation.Type))
        {
            return;
        }

        foreach (IArgumentOperation argument in objectCreation.Arguments)
        {
            if (argument.Parameter?.Name == "commandText" && IsUnsafeSqlExpression(argument.Value))
            {
                Report(context, argument.Value.Syntax);
            }
        }
    }

    private static void AnalyzeUnboundObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        // Bound constructors are handled by the operation callback. Dynamic dispatch and
        // incomplete IDE code have no IObjectCreationOperation, but still expose the command
        // type and argument syntax. Keep their diagnostics without duplicating bound calls.
        if (
            context.SemanticModel.GetOperation(creation, context.CancellationToken)
                is IObjectCreationOperation
            || !IsCommandType(
                context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type
            )
            || creation.ArgumentList is not { } argumentList
        )
        {
            return;
        }

        for (int index = 0; index < argumentList.Arguments.Count; index++)
        {
            ArgumentSyntax argument = argumentList.Arguments[index];
            string? name = argument.NameColon?.Name.Identifier.ValueText;
            if (name == "commandText" || (name is null && index == 0))
            {
                ExpressionSyntax expression = argument.Expression;
                if (
                    IsUnsafeSqlExpression(
                        expression,
                        context
                            .SemanticModel.GetConstantValue(expression, context.CancellationToken)
                            .HasValue,
                        context
                            .SemanticModel.GetTypeInfo(expression, context.CancellationToken)
                            .Type
                    )
                )
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DotRocksDiagnosticDescriptors.UnsafeCommandText,
                            expression.GetLocation()
                        )
                    );
                }
            }
        }
    }

    private static bool IsUnsafeSqlExpression(IOperation operation) =>
        operation.Syntax is ExpressionSyntax expression
        && IsUnsafeSqlExpression(expression, operation.ConstantValue.HasValue, operation.Type);

    private static bool IsUnsafeSqlExpression(
        ExpressionSyntax expression,
        bool isConstant,
        ITypeSymbol? type
    )
    {
        // A compile-time constant (literal or fully constant interpolation/concatenation) is safe.
        if (isConstant)
        {
            return false;
        }

        ExpressionSyntax unwrapped = Unwrap(expression);

        return unwrapped switch
        {
            InterpolatedStringExpressionSyntax => true,
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) =>
                type?.SpecialType == SpecialType.System_String,
            _ => false,
        };
    }

    private static bool IsCommandType(ITypeSymbol? type) =>
        AnalyzerSyntaxHelpers.IsNamedType(type, DataCommandTypeName)
        || AnalyzerSyntaxHelpers.IsNamedType(type, FlightSqlCommandTypeName);

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) =>
        expression is ParenthesizedExpressionSyntax parenthesized
            ? Unwrap(parenthesized.Expression)
            : expression;

    private static void Report(OperationAnalysisContext context, SyntaxNode node) =>
        context.ReportDiagnostic(
            Diagnostic.Create(DotRocksDiagnosticDescriptors.UnsafeCommandText, node.GetLocation())
        );
}
