using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

/// <summary>
/// StarRocks is an MPP analytics engine, so JOIN and GROUP BY/HAVING/aggregate
/// translation must produce SQL rather than throw. These tests pin the generated
/// SQL shape; live behavior is exercised by the integration test suite.
/// </summary>
public sealed class DotRocksJoinAndAggregateQueryTests
{
    [Fact]
    public void InnerJoin_GeneratesInnerJoinSql()
    {
        using var context = CreateContext();

        string sql = context
            .Widgets.Join(
                context.WidgetDetails,
                widget => widget.Id,
                detail => detail.WidgetId,
                (widget, detail) => new { widget.Id, detail.Description }
            )
            .ToQueryString();

        Assert.Contains("INNER JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`unit_db`.`widget_details`", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void LeftJoin_GeneratesLeftJoinSql()
    {
        using var context = CreateContext();

        string sql = context
            .Widgets.GroupJoin(
                context.WidgetDetails,
                widget => widget.Id,
                detail => detail.WidgetId,
                (widget, details) => new { widget, details }
            )
            .SelectMany(
                row => row.details.DefaultIfEmpty(),
                (row, detail) => new { row.widget.Id, detail!.Description }
            )
            .ToQueryString();

        Assert.Contains("LEFT JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GroupBy_WithCount_GeneratesGroupBySql()
    {
        using var context = CreateContext();

        string sql = context
            .Widgets.GroupBy(widget => widget.Category)
            .Select(group => new { Category = group.Key, Count = group.Count() })
            .ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT(*)", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GroupBy_WithHaving_GeneratesHavingSql()
    {
        using var context = CreateContext();

        string sql = context
            .Widgets.GroupBy(widget => widget.Category)
            .Where(group => group.Count() > 5)
            .Select(group => new { Category = group.Key, Count = group.Count() })
            .ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HAVING", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Aggregate_SumAndMax_GenerateAggregateSql()
    {
        using var context = CreateContext();

        string sql = context
            .Widgets.GroupBy(widget => widget.Category)
            .Select(group => new
            {
                group.Key,
                Total = group.Sum(widget => widget.Id),
                Largest = group.Max(widget => widget.Id),
            })
            .ToQueryString();

        Assert.Contains("SUM(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MAX(", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static UnitContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<UnitContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");
        return new UnitContext(optionsBuilder.Options);
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class UnitContext(DbContextOptions<UnitContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        public DbSet<WidgetDetail> WidgetDetails => Set<WidgetDetail>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().ToTable("widgets", "unit_db").HasKey(widget => widget.Id);
            modelBuilder.Entity<Widget>().Property(widget => widget.Id).ValueGeneratedNever();
            modelBuilder
                .Entity<WidgetDetail>()
                .ToTable("widget_details", "unit_db")
                .HasKey(detail => detail.Id);
            modelBuilder.Entity<WidgetDetail>().Property(detail => detail.Id).ValueGeneratedNever();
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class Widget
    {
        public int Id { get; set; }

        public int Category { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class WidgetDetail
    {
        public int Id { get; set; }

        public int WidgetId { get; set; }

        public string Description { get; set; } = string.Empty;
    }
}
