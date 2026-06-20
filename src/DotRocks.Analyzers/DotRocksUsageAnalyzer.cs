using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers;

/// <summary>
/// Reports insecure or unsupported DotRocks usage patterns that are visible in source.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DotRocksUsageAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic id for insecure Stream Load HTTP endpoints with credentials.
    /// </summary>
    public const string InsecureStreamLoadEndpointDiagnosticId = "DTR0001";

    /// <summary>
    /// Diagnostic id for EF writable keys that are not configured with ValueGeneratedNever().
    /// </summary>
    public const string MissingValueGeneratedNeverDiagnosticId = "DTR0002";

    /// <summary>
    /// Diagnostic id for unsupported EF binary and varbinary mappings.
    /// </summary>
    public const string UnsupportedBinaryMappingDiagnosticId = "DTR0003";

    /// <summary>
    /// Diagnostic id for visible transaction double-completion.
    /// </summary>
    public const string TransactionDoubleCompletionDiagnosticId = "DTR0004";

    private static readonly DiagnosticDescriptor InsecureStreamLoadEndpointRule = new(
        InsecureStreamLoadEndpointDiagnosticId,
        "Avoid insecure Stream Load endpoints with credentials",
        "Connection string uses an HTTP Stream Load endpoint with credentials",
        "Security",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Use HTTPS when a DotRocks connection string contains Stream Load credentials."
    );

    private static readonly DiagnosticDescriptor MissingValueGeneratedNeverRule = new(
        MissingValueGeneratedNeverDiagnosticId,
        "Configure EF writable keys with ValueGeneratedNever",
        "Entity key property '{0}' is not configured with ValueGeneratedNever()",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DotRocks EF Core SaveChanges supports explicit values only; writable key properties should be configured with ValueGeneratedNever()."
    );

    private static readonly DiagnosticDescriptor UnsupportedBinaryMappingRule = new(
        UnsupportedBinaryMappingDiagnosticId,
        "EF binary mapping is unsupported",
        "DotRocks EF Core does not support '{0}' mapping yet",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Binary and varbinary EF mappings are unsupported until the EF read/write surface is verified end to end."
    );

    private static readonly DiagnosticDescriptor TransactionDoubleCompletionRule = new(
        TransactionDoubleCompletionDiagnosticId,
        "Transaction is completed more than once",
        "Transaction variable '{0}' is completed more than once",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DotRocks SQL transactions and Stream Load transactions are single-use after commit or rollback."
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            InsecureStreamLoadEndpointRule,
            MissingValueGeneratedNeverRule,
            UnsupportedBinaryMappingRule,
            TransactionDoubleCompletionRule
        );

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
        context.RegisterSyntaxNodeAction(
            AnalyzeObjectCreation,
            SyntaxKind.ObjectCreationExpression
        );
        context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeBlock, SyntaxKind.Block);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        AnalyzeUnsupportedColumnType(context, invocation);
        AnalyzeInsecureConnectionStringFactory(context, invocation);
    }

    private static void AnalyzeUnsupportedColumnType(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation
    )
    {
        if (!IsMemberInvocation(invocation, "HasColumnType"))
        {
            return;
        }

        if (
            invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } expression
            || GetConstantString(context, expression) is not { } storeType
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
                Diagnostic.Create(UnsupportedBinaryMappingRule, expression.GetLocation(), storeType)
            );
        }
    }

    private static void AnalyzeInsecureConnectionStringFactory(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation
    )
    {
        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
        {
            if (
                GetConstantString(context, argument.Expression) is { } connectionString
                && IsInsecureStreamLoadConnectionString(connectionString)
            )
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InsecureStreamLoadEndpointRule,
                        argument.Expression.GetLocation()
                    )
                );
            }
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var objectCreation = (ObjectCreationExpressionSyntax)context.Node;
        ITypeSymbol? type = context.SemanticModel.GetTypeInfo(objectCreation).Type;

        if (IsDotRocksConnectionStringConsumer(type))
        {
            foreach (ArgumentSyntax argument in objectCreation.ArgumentList?.Arguments ?? [])
            {
                if (
                    GetConstantString(context, argument.Expression) is { } connectionString
                    && IsInsecureStreamLoadConnectionString(connectionString)
                )
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            InsecureStreamLoadEndpointRule,
                            argument.Expression.GetLocation()
                        )
                    );
                }
            }
        }

        if (
            IsNamedType(type, "DotRocks.Data.DotRocksConnectionStringBuilder")
            && objectCreation.Initializer is not null
        )
        {
            AnalyzeConnectionStringBuilderInitializer(context, objectCreation.Initializer);
        }
    }

    private static void AnalyzeConnectionStringBuilderInitializer(
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
                    && GetConstantString(context, assignment.Right) is { } endpoint
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
                    ) && !string.IsNullOrEmpty(GetConstantString(context, assignment.Right))
                )
                {
                    hasPassword = true;
                }
            }
        }

        if (httpEndpointExpression is not null && hasPassword)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InsecureStreamLoadEndpointRule,
                    httpEndpointExpression.GetLocation()
                )
            );
        }
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
            .Where(invocation => IsMemberInvocation(invocation, "ValueGeneratedNever"))
            .Select(invocation => GetPropertyInvocationFromChain(invocation))
            .Where(invocation => invocation is not null)
            .Select(invocation => CreateEfPropertyReference(context, invocation!))
            .Where(property => property.PropertyName is not null)
            .ToArray();

        foreach (
            InvocationExpressionSyntax hasKeyInvocation in method
                .Body.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation => IsMemberInvocation(invocation, "HasKey"))
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
                        MissingValueGeneratedNeverRule,
                        keyProperty.Location,
                        keyProperty.PropertyName
                    )
                );
            }
        }
    }

    private static void AnalyzeBlock(SyntaxNodeAnalysisContext context)
    {
        var block = (BlockSyntax)context.Node;
        var completions = new Dictionary<string, Location>(StringComparer.Ordinal);
        foreach (
            InvocationExpressionSyntax invocation in block
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
        )
        {
            if (
                invocation.Expression is not MemberAccessExpressionSyntax memberAccess
                || memberAccess.Expression is not IdentifierNameSyntax receiver
                || !IsCompletionMethod(memberAccess.Name.Identifier.ValueText)
                || !IsDotRocksTransactionType(context.SemanticModel.GetTypeInfo(receiver).Type)
            )
            {
                continue;
            }

            string variableName = receiver.Identifier.ValueText;
            if (completions.ContainsKey(variableName))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        TransactionDoubleCompletionRule,
                        memberAccess.Name.GetLocation(),
                        variableName
                    )
                );
                continue;
            }

            completions.Add(variableName, memberAccess.Name.GetLocation());
        }
    }

    private static bool IsMemberInvocation(InvocationExpressionSyntax invocation, string name) =>
        invocation.Expression is MemberAccessExpressionSyntax memberAccess
        && string.Equals(memberAccess.Name.Identifier.ValueText, name, StringComparison.Ordinal);

    private static bool IsCompletionMethod(string methodName) =>
        string.Equals(methodName, "Commit", StringComparison.Ordinal)
        || string.Equals(methodName, "CommitAsync", StringComparison.Ordinal)
        || string.Equals(methodName, "Rollback", StringComparison.Ordinal)
        || string.Equals(methodName, "RollbackAsync", StringComparison.Ordinal);

    private static string? GetConstantString(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        Optional<object?> value = context.SemanticModel.GetConstantValue(expression);
        return value.HasValue ? value.Value as string : null;
    }

    private static bool IsInsecureStreamLoadConnectionString(string value) =>
        value.IndexOf("stream load endpoint=http://", StringComparison.OrdinalIgnoreCase) >= 0
        && value.IndexOf("password=", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsDotRocksConnectionStringConsumer(ITypeSymbol? type) =>
        IsNamedType(type, "DotRocks.Data.DotRocksConnection")
        || IsNamedType(type, "DotRocks.Data.DotRocksDataSource")
        || IsNamedType(type, "DotRocks.Data.DotRocksConnectionStringBuilder")
        || IsNamedType(type, "DotRocks.Data.Loading.DotRocksStreamLoadClient");

    private static bool IsDotRocksTransactionType(ITypeSymbol? type) =>
        IsNamedType(type, "DotRocks.Data.DotRocksTransaction")
        || IsNamedType(type, "DotRocks.Data.Loading.DotRocksStreamLoadTransaction");

    private static bool IsNamedType(ITypeSymbol? type, string metadataName) =>
        string.Equals(
            type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "global::" + metadataName,
            StringComparison.Ordinal
        );

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
        if (IsMemberInvocation(invocation, "Property"))
        {
            return invocation;
        }

        return invocation
            .Expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(candidate => IsMemberInvocation(candidate, "Property"));
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
        if (receiverType is INamedTypeSymbol namedReceiverType)
        {
            string receiverMetadataName = namedReceiverType.ConstructedFrom.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            );
            if (
                string.Equals(
                    receiverMetadataName,
                    "global::Microsoft.EntityFrameworkCore.EntityTypeBuilder<TEntity>",
                    StringComparison.Ordinal
                )
                && namedReceiverType.TypeArguments.Length == 1
            )
            {
                return namedReceiverType
                    .TypeArguments[0]
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
        }

        InvocationExpressionSyntax? entityInvocation = memberAccess
            .Expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(candidate => IsMemberInvocation(candidate, "Entity"));
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
