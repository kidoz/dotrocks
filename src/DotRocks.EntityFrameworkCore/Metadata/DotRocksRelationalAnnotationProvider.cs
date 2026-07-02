using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotRocks.EntityFrameworkCore.Metadata;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksRelationalAnnotationProvider(
    RelationalAnnotationProviderDependencies dependencies
) : RelationalAnnotationProvider(dependencies)
{
    public override IEnumerable<IAnnotation> For(ITable table, bool designTime)
    {
        foreach (IAnnotation annotation in base.For(table, designTime))
        {
            yield return annotation;
        }

        foreach (
            DotRocksTableShapeAnnotation tableShapeAnnotation in DotRocksTableShapeAnnotations.All
        )
        {
            IAnnotation? annotation = GetTableShapeAnnotation(table, tableShapeAnnotation.Name);
            if (annotation is not null)
            {
                yield return annotation;
            }
        }
    }

    private static IAnnotation? GetTableShapeAnnotation(ITable table, string annotationName)
    {
        IAnnotation? result = null;
        foreach (
            IEntityType entityType in table
                .EntityTypeMappings.Select(mapping => mapping.TypeBase)
                .OfType<IEntityType>()
        )
        {
            IAnnotation? annotation = entityType.FindAnnotation(annotationName);
            if (annotation is null)
            {
                continue;
            }

            if (
                result is not null
                && !DotRocksAnnotationValues.AreEqual(result.Value, annotation.Value)
            )
            {
                throw new NotSupportedException(
                    $"DotRocks EF Core migrations do not support conflicting '{annotationName}' table-shape annotations on shared table '{table.SchemaQualifiedName}'."
                );
            }

            result = annotation;
        }

        return result;
    }
}
