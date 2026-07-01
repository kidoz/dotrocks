using System.Diagnostics.CodeAnalysis;
using DotRocks.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksAnnotationCodeGeneratorTests
{
    private static readonly string[] IdColumn = ["id"];
    private static readonly string[] SortColumns = ["name", "id"];

    [Fact]
    public void GenerateFluentApiCalls_RandomDistribution_EmitsDistributedRandomly()
    {
        IEntityType entityType = BuildEntityType(entity =>
        {
            entity.SetAnnotation("DotRocks:RandomDistribution", true);
            entity.SetAnnotation("DotRocks:DistributionBuckets", 7);
        });

        MethodCallCodeFragment[] fragments = Generate(entityType);

        MethodCallCodeFragment fragment = Assert.Single(
            fragments,
            f => f.Method == "DistributedRandomly"
        );
        Assert.Equal([7], fragment.Arguments);
        // A random-distributed table must not regenerate a (broken, zero-column) hash call.
        Assert.DoesNotContain(fragments, f => f.Method == "HasStarRocksHashDistribution");
    }

    [Fact]
    public void GenerateFluentApiCalls_HashDistribution_StillEmitsHashCall()
    {
        IEntityType entityType = BuildEntityType(entity =>
        {
            entity.SetAnnotation("DotRocks:DistributionColumns", IdColumn);
            entity.SetAnnotation("DotRocks:DistributionBuckets", 4);
        });

        MethodCallCodeFragment[] fragments = Generate(entityType);

        MethodCallCodeFragment fragment = Assert.Single(
            fragments,
            f => f.Method == "HasStarRocksHashDistribution"
        );
        Assert.Equal([4, "id"], fragment.Arguments);
    }

    [Fact]
    public void GenerateFluentApiCalls_SortKey_EmitsHasSortKey()
    {
        IEntityType entityType = BuildEntityType(entity =>
            entity.SetAnnotation("DotRocks:SortKeyColumns", SortColumns)
        );

        MethodCallCodeFragment[] fragments = Generate(entityType);

        MethodCallCodeFragment fragment = Assert.Single(fragments, f => f.Method == "HasSortKey");
        Assert.Equal(["name", "id"], fragment.Arguments);
    }

    [Fact]
    public void GenerateFluentApiCalls_Properties_EmitsHasStarRocksPropertyPerEntry()
    {
        IEntityType entityType = BuildEntityType(entity =>
            entity.SetAnnotation(
                "DotRocks:Properties",
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bloom_filter_columns"] = "name",
                    ["compression"] = "LZ4",
                }
            )
        );

        MethodCallCodeFragment[] fragments = Generate(entityType);

        Assert.Contains(
            fragments,
            f =>
                f.Method == "HasStarRocksProperty"
                && f.Arguments.Count == 2
                && Equals(f.Arguments[0], "bloom_filter_columns")
                && Equals(f.Arguments[1], "name")
        );
        Assert.Contains(
            fragments,
            f =>
                f.Method == "HasStarRocksProperty"
                && f.Arguments.Count == 2
                && Equals(f.Arguments[0], "compression")
                && Equals(f.Arguments[1], "LZ4")
        );
    }

    private static MethodCallCodeFragment[] Generate(IEntityType entityType)
    {
        using ServiceProvider provider = CreateDesignServiceProvider();
        var generator = provider.GetRequiredService<IAnnotationCodeGenerator>();
        Dictionary<string, IAnnotation> annotations = entityType
            .GetAnnotations()
            .ToDictionary(annotation => annotation.Name, annotation => (IAnnotation)annotation);
        return generator.GenerateFluentApiCalls(entityType, annotations).ToArray();
    }

    private static IEntityType BuildEntityType(Action<IMutableEntityType> configure)
    {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        IMutableEntityType entityType = modelBuilder.Entity<CodegenWidget>().Metadata;
        entityType.SetPrimaryKey(entityType.FindProperty(nameof(CodegenWidget.Id))!);
        entityType.SetTableName("widgets");
        configure(entityType);
        return modelBuilder.FinalizeModel().FindEntityType(typeof(CodegenWidget))!;
    }

    private static ServiceProvider CreateDesignServiceProvider()
    {
        var services = new ServiceCollection();
        new DotRocksDesignTimeServices().ConfigureDesignTimeServices(services);
        return services.BuildServiceProvider();
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core instantiates this entity type through the model builder."
    )]
    private sealed class CodegenWidget
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
