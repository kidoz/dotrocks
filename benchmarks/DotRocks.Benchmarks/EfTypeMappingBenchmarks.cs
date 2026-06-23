using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace DotRocks.Benchmarks;

/// <summary>
/// Benchmarks the EF Core type-mapping resolution hot path backed by the
/// <see cref="System.Collections.Frozen.FrozenDictionary{TKey, TValue}"/> CLR-type lookup table.
/// Each invocation resolves a representative spread of CLR and store types through the public
/// <see cref="IRelationalTypeMappingSource"/> the provider exposes at runtime.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Local)]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "BenchmarkDotNet owns the lifecycle; the context is disposed in [GlobalCleanup]."
)]
public class EfTypeMappingBenchmarks
{
    private static readonly Type[] ClrTypes =
    [
        typeof(bool),
        typeof(int),
        typeof(long),
        typeof(double),
        typeof(decimal),
        typeof(string),
        typeof(DateTime),
        typeof(Guid),
    ];

    private static readonly string[] StoreTypes =
    [
        "boolean",
        "int",
        "bigint",
        "double",
        "decimal(18, 2)",
        "varchar(128)",
        "datetime",
        "largeint",
    ];

    private DbContext _context = null!;
    private IRelationalTypeMappingSource _mappingSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        _context = new BenchmarkContext();
        _mappingSource = _context.GetService<IRelationalTypeMappingSource>();
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public int FindClrTypeMapping()
    {
        int resolved = 0;
        foreach (Type clrType in ClrTypes)
        {
            if (_mappingSource.FindMapping(clrType) is not null)
            {
                resolved++;
            }
        }

        return resolved;
    }

    [Benchmark]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "BenchmarkDotNet requires public instance benchmark methods."
    )]
    public int FindStoreTypeMapping()
    {
        int resolved = 0;
        foreach (string storeType in StoreTypes)
        {
            if (_mappingSource.FindMapping(storeType) is not null)
            {
                resolved++;
            }
        }

        return resolved;
    }

    private sealed class BenchmarkContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");
    }
}
