using DotRocks.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotRocks.EntityFrameworkCore.Design;

/// <summary>
/// Generates design-time fluent API calls for DotRocks-specific model annotations. The annotation
/// names, value coercion, and fluent-call mapping live in the runtime provider's table-shape
/// annotation registry, so this generator cannot drift from the migrations SQL generator.
/// </summary>
public sealed class DotRocksAnnotationCodeGenerator(
    AnnotationCodeGeneratorDependencies dependencies
) : AnnotationCodeGenerator(dependencies)
{
    /// <inheritdoc />
    public override IReadOnlyList<MethodCallCodeFragment> GenerateFluentApiCalls(
        IEntityType entityType,
        IDictionary<string, IAnnotation> annotations
    )
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(annotations);

        List<MethodCallCodeFragment> fragments = base.GenerateFluentApiCalls(
                entityType,
                annotations
            )
            .ToList();
        fragments.AddRange(DotRocksTableShapeAnnotations.GenerateFluentApiCalls(annotations));
        return fragments;
    }
}
