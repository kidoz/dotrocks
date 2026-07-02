using System.Reflection;
using DotRocks.EntityFrameworkCore.Metadata;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksTableShapeAnnotationsTests
{
    [Fact]
    public void Registry_CoversEveryDeclaredAnnotationName()
    {
        // Every DotRocksAnnotationNames constant must have exactly one registry descriptor, so a
        // new annotation cannot be added without wiring it through the provider, the validator,
        // the migrations SQL generator, and the design-time code generator.
        string[] declaredNames =
        [
            .. typeof(DotRocksAnnotationNames)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral)
                .Select(field => (string)field.GetRawConstantValue()!)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];
        string[] registeredNames =
        [
            .. DotRocksTableShapeAnnotations
                .All.Select(annotation => annotation.Name)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];

        Assert.NotEmpty(declaredNames);
        Assert.Equal(declaredNames, registeredNames);
    }

    [Fact]
    public void Registry_UsesDistinctAnnotationNames()
    {
        string[] names = [.. DotRocksTableShapeAnnotations.All.Select(a => a.Name)];

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }
}
