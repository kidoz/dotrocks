using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;

namespace DotRocks.Benchmarks;

/// <summary>Server-backed benchmark for EF Core query materialization.</summary>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.ServerBacked)]
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
public class EfMaterializationBenchmarks
{
    private BenchmarkContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        string connectionString = BenchmarkServer.EnsureDatabase();
        BenchmarkServer.Execute(connectionString, "DROP TABLE IF EXISTS ef_materialization_rows");
        BenchmarkServer.Execute(
            connectionString,
            "CREATE TABLE ef_materialization_rows (`id` BIGINT NOT NULL, `value` VARCHAR(64) NOT NULL) "
                + "DUPLICATE KEY (`id`) DISTRIBUTED BY HASH (`id`) BUCKETS 1 "
                + "PROPERTIES (\"replication_num\" = \"1\")"
        );
        BenchmarkServer.Execute(
            connectionString,
            "INSERT INTO ef_materialization_rows SELECT number, concat('row-', number) "
                + "FROM numbers('number' = '1000')"
        );

        var options = new DbContextOptionsBuilder<BenchmarkContext>()
            .UseStarRocks(connectionString)
            .Options;
        _context = new BenchmarkContext(options);
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark]
    public Task<List<BenchmarkRow>> MaterializeEfQuery() =>
        _context.Rows.AsNoTracking().OrderBy(row => row.Id).ToListAsync();

    [SuppressMessage(
        "Design",
        "CA1034:Do not nest types",
        Justification = "The benchmark entity is scoped to its benchmark fixture."
    )]
    public sealed class BenchmarkRow
    {
        public long Id { get; set; }

        public string Value { get; set; } = string.Empty;
    }

    private sealed class BenchmarkContext(DbContextOptions<BenchmarkContext> options)
        : DbContext(options)
    {
        public DbSet<BenchmarkRow> Rows => Set<BenchmarkRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BenchmarkRow>(entity =>
            {
                entity.ToTable("ef_materialization_rows");
                entity.HasKey(row => row.Id);
                entity.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
                entity.Property(row => row.Value).HasColumnName("value");
            });
        }
    }
}
