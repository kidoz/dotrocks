using DotRocks.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotRocks.EntityFrameworkCore.Design;

/// <summary>
/// Generates design-time fluent API calls for DotRocks-specific model annotations.
/// </summary>
public sealed class DotRocksAnnotationCodeGenerator(
    AnnotationCodeGeneratorDependencies dependencies
) : AnnotationCodeGenerator(dependencies)
{
    private const string KeyModelAnnotation = "DotRocks:KeyModel";
    private const string KeyColumnsAnnotation = "DotRocks:KeyColumns";
    private const string DistributionColumnsAnnotation = "DotRocks:DistributionColumns";
    private const string DistributionBucketsAnnotation = "DotRocks:DistributionBuckets";
    private const string ReplicationNumAnnotation = "DotRocks:ReplicationNum";

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
        string[]? keyColumns = RemoveStringArrayAnnotation(annotations, KeyColumnsAnnotation);
        object? keyModel = RemoveAnnotation(annotations, KeyModelAnnotation);
        string[]? distributionColumns = RemoveStringArrayAnnotation(
            annotations,
            DistributionColumnsAnnotation
        );
        int? distributionBuckets = RemoveIntAnnotation(annotations, DistributionBucketsAnnotation);
        int? replicationNum = RemoveIntAnnotation(annotations, ReplicationNumAnnotation);

        if (keyModel is not null)
        {
            fragments.Add(
                new MethodCallCodeFragment(
                    GetKeyModelMethodName(keyModel),
                    ToObjectArray(keyColumns ?? [])
                )
            );
        }

        if (distributionColumns is not null || distributionBuckets is not null)
        {
            fragments.Add(
                new MethodCallCodeFragment(
                    "HasStarRocksHashDistribution",
                    ToObjectArray(distributionBuckets ?? 1, distributionColumns ?? [])
                )
            );
        }

        if (replicationNum is not null)
        {
            fragments.Add(new MethodCallCodeFragment("HasStarRocksReplicationNum", replicationNum));
        }

        return fragments;
    }

    private static string GetKeyModelMethodName(object keyModel) =>
        keyModel switch
        {
            DotRocksTableKeyModel.DuplicateKey => "HasStarRocksDuplicateKey",
            DotRocksTableKeyModel.PrimaryKey => "HasStarRocksPrimaryKey",
            DotRocksTableKeyModel.UniqueKey => "HasStarRocksUniqueKey",
            string text
                when string.Equals(text, "DUPLICATE KEY", StringComparison.OrdinalIgnoreCase) =>
                "HasStarRocksDuplicateKey",
            string text
                when string.Equals(text, "PRIMARY KEY", StringComparison.OrdinalIgnoreCase) =>
                "HasStarRocksPrimaryKey",
            string text
                when string.Equals(text, "UNIQUE KEY", StringComparison.OrdinalIgnoreCase) =>
                "HasStarRocksUniqueKey",
            _ => throw new NotSupportedException(
                $"DotRocks EF Core design-time services do not support StarRocks table key model '{keyModel}'."
            ),
        };

    private static object? RemoveAnnotation(
        IDictionary<string, IAnnotation> annotations,
        string annotationName
    )
    {
        if (!annotations.TryGetValue(annotationName, out IAnnotation? annotation))
        {
            return null;
        }

        annotations.Remove(annotationName);
        return annotation.Value;
    }

    private static string[]? RemoveStringArrayAnnotation(
        IDictionary<string, IAnnotation> annotations,
        string annotationName
    )
    {
        object? value = RemoveAnnotation(annotations, annotationName);
        return value switch
        {
            null => null,
            string[] strings => strings,
            IReadOnlyList<string> strings => strings.ToArray(),
            _ => throw new NotSupportedException(
                $"DotRocks EF Core design-time services require '{annotationName}' to be string column names."
            ),
        };
    }

    private static int? RemoveIntAnnotation(
        IDictionary<string, IAnnotation> annotations,
        string annotationName
    )
    {
        object? value = RemoveAnnotation(annotations, annotationName);
        return value switch
        {
            null => null,
            int intValue => intValue,
            _ => throw new NotSupportedException(
                $"DotRocks EF Core design-time services require '{annotationName}' to be an integer."
            ),
        };
    }

    private static object[] ToObjectArray(params string[] values) =>
        values.Cast<object>().ToArray();

    private static object[] ToObjectArray(int first, string[] values)
    {
        object[] result = new object[values.Length + 1];
        result[0] = first;
        for (int i = 0; i < values.Length; i++)
        {
            result[i + 1] = values[i];
        }

        return result;
    }
}
