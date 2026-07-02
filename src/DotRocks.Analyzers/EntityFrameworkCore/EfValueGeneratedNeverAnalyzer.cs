using System.Collections.Immutable;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.EntityFrameworkCore;

/// <summary>
/// Reports EF Core writable key properties without a visible ValueGeneratedNever() configuration.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EfValueGeneratedNeverAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [DotRocksDiagnosticDescriptors.MissingValueGeneratedNever];

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

        var configuredProperties = method
            .Body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation =>
                AnalyzerSyntaxHelpers.IsMemberInvocation(invocation, "ValueGeneratedNever")
            )
            .Select(GetPropertyInvocationFromChain)
            .Where(invocation => invocation is not null)
            .Select(invocation => CreateEfPropertyReference(context, invocation!))
            .Where(property => property.PropertyName is not null)
            .ToArray();

        foreach (
            InvocationExpressionSyntax hasKeyInvocation in method
                .Body.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation => AnalyzerSyntaxHelpers.IsMemberInvocation(invocation, "HasKey"))
        )
        {
            string? entityTypeName = GetEntityTypeName(context, hasKeyInvocation);
            foreach (EfPropertyReference keyProperty in GetKeyProperties(context, hasKeyInvocation))
            {
                if (
                    configuredProperties.Any(property =>
                        IsSameProperty(entityTypeName, keyProperty.PropertyName, property)
                    )
                )
                {
                    continue;
                }

                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DotRocksDiagnosticDescriptors.MissingValueGeneratedNever,
                        keyProperty.Location,
                        keyProperty.PropertyName
                    )
                );
            }
        }
    }

    private static EfPropertyReference CreateEfPropertyReference(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax propertyInvocation
    )
    {
        string? entityTypeName = GetEntityTypeName(context, propertyInvocation);
        ExpressionSyntax? expression = propertyInvocation
            .ArgumentList.Arguments.FirstOrDefault()
            ?.Expression;
        EfPropertyReference property = ExtractLambdaMemberReference(
            expression,
            fallbackLocation: propertyInvocation.GetLocation()
        );
        return property.WithEntityTypeName(entityTypeName);
    }

    private static IEnumerable<EfPropertyReference> GetKeyProperties(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax hasKeyInvocation
    )
    {
        ExpressionSyntax? expression = hasKeyInvocation
            .ArgumentList.Arguments.FirstOrDefault()
            ?.Expression;
        string? entityTypeName = GetEntityTypeName(context, hasKeyInvocation);

        foreach (
            EfPropertyReference property in ExtractLambdaMemberReferences(
                expression,
                hasKeyInvocation.GetLocation()
            )
        )
        {
            yield return property.WithEntityTypeName(entityTypeName);
        }
    }

    private static EfPropertyReference ExtractLambdaMemberReference(
        ExpressionSyntax? expression,
        Location fallbackLocation
    ) => ExtractLambdaMemberReferences(expression, fallbackLocation).FirstOrDefault();

    private static IEnumerable<EfPropertyReference> ExtractLambdaMemberReferences(
        ExpressionSyntax? expression,
        Location fallbackLocation
    )
    {
        if (expression is ParenthesizedLambdaExpressionSyntax parenthesizedLambda)
        {
            expression = parenthesizedLambda.Body as ExpressionSyntax;
        }
        else if (expression is SimpleLambdaExpressionSyntax simpleLambda)
        {
            expression = simpleLambda.Body as ExpressionSyntax;
        }

        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            yield return new EfPropertyReference(
                null,
                memberAccess.Name.Identifier.ValueText,
                memberAccess.Name.GetLocation()
            );
            yield break;
        }

        if (expression is AnonymousObjectCreationExpressionSyntax anonymousObject)
        {
            foreach (
                AnonymousObjectMemberDeclaratorSyntax initializer in anonymousObject.Initializers
            )
            {
                if (initializer.Expression is MemberAccessExpressionSyntax initializerMember)
                {
                    yield return new EfPropertyReference(
                        null,
                        initializerMember.Name.Identifier.ValueText,
                        initializerMember.Name.GetLocation()
                    );
                }
            }

            yield break;
        }

        yield return new EfPropertyReference(null, "unknown", fallbackLocation);
    }

    private static InvocationExpressionSyntax? GetPropertyInvocationFromChain(
        InvocationExpressionSyntax invocation
    )
    {
        if (AnalyzerSyntaxHelpers.IsMemberInvocation(invocation, "Property"))
        {
            return invocation;
        }

        return invocation
            .Expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(candidate =>
                AnalyzerSyntaxHelpers.IsMemberInvocation(candidate, "Property")
            );
    }

    private static string? GetEntityTypeName(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation
    )
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        ITypeSymbol? receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
        if (
            receiverType is INamedTypeSymbol namedReceiverType
            && AnalyzerSyntaxHelpers.IsNamedType(
                namedReceiverType,
                "Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder",
                arity: 1
            )
        )
        {
            return namedReceiverType
                .TypeArguments[0]
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        InvocationExpressionSyntax? entityInvocation = memberAccess
            .Expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(candidate =>
                AnalyzerSyntaxHelpers.IsMemberInvocation(candidate, "Entity")
            );
        if (
            entityInvocation?.Expression is MemberAccessExpressionSyntax entityMember
            && entityMember.Name is GenericNameSyntax genericName
            && genericName.TypeArgumentList.Arguments.Count == 1
        )
        {
            return context
                .SemanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0])
                .Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        return null;
    }

    private static bool IsSameProperty(
        string? entityTypeName,
        string propertyName,
        EfPropertyReference configuredProperty
    ) =>
        string.Equals(propertyName, configuredProperty.PropertyName, StringComparison.Ordinal)
        && (
            entityTypeName is null
            || configuredProperty.EntityTypeName is null
            || string.Equals(
                entityTypeName,
                configuredProperty.EntityTypeName,
                StringComparison.Ordinal
            )
        );

    private readonly struct EfPropertyReference(
        string? entityTypeName,
        string propertyName,
        Location location
    )
    {
        public string? EntityTypeName { get; } = entityTypeName;

        public string PropertyName { get; } = propertyName;

        public Location Location { get; } = location;

        public EfPropertyReference WithEntityTypeName(string? entityTypeName) =>
            new(entityTypeName, PropertyName, Location);
    }
}
