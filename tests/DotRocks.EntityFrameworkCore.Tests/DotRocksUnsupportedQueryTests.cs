using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksUnsupportedQueryTests
{
    [Fact]
    public void Join_ThrowsNotSupportedException()
    {
        using var context = CreateContext();

        Assert.Throws<NotSupportedException>(() =>
            context
                .Widgets.Join(
                    context.WidgetDetails,
                    widget => widget.Id,
                    detail => detail.WidgetId,
                    (widget, detail) => new { widget.Id, detail.Description }
                )
                .ToQueryString()
        );
    }

    [Fact]
    public void Include_ThrowsNotSupportedException()
    {
        using var context = CreateContext();

        Assert.Throws<NotSupportedException>(() =>
            context.Widgets.Include(widget => widget.Details).ToQueryString()
        );
    }

    [Fact]
    public void GroupBy_ThrowsNotSupportedException()
    {
        using var context = CreateContext();

        Assert.Throws<NotSupportedException>(() =>
            context
                .Widgets.GroupBy(widget => widget.Category)
                .Select(group => new { Category = group.Key, Count = group.Count() })
                .ToQueryString()
        );
    }

    [Fact]
    public void ClientMethodCall_ThrowsInvalidOperationException()
    {
        using var context = CreateContext();

        Assert.Throws<InvalidOperationException>(() =>
            context.Widgets.Where(widget => IsInteresting(widget.Name)).ToQueryString()
        );
    }

    [Fact]
    public async Task ExecuteUpdateAsync_ThrowsNotSupportedException()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            context
                .Widgets.Where(widget => widget.Id == 1)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(widget => widget.Name, "updated"),
                    TestContext.Current.CancellationToken
                )
        );
    }

    [Fact]
    public async Task ExecuteDeleteAsync_ThrowsNotSupportedException()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            context
                .Widgets.Where(widget => widget.Id == 1)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken)
        );
    }

    private static UnitContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<UnitContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");
        return new UnitContext(optionsBuilder.Options);
    }

    private static bool IsInteresting(string value) =>
        string.Equals(value, "interesting", StringComparison.Ordinal);

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class UnitContext(DbContextOptions<UnitContext> options) : DbContext(options)
    {
        public DbSet<UnitWidget> Widgets => Set<UnitWidget>();

        public DbSet<UnitWidgetDetail> WidgetDetails => Set<UnitWidgetDetail>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .Entity<UnitWidget>()
                .ToTable("widgets", "unit_db")
                .HasKey(widget => widget.Id);
            modelBuilder
                .Entity<UnitWidgetDetail>()
                .ToTable("widget_details", "unit_db")
                .HasKey(detail => detail.Id);
            modelBuilder
                .Entity<UnitWidget>()
                .HasMany(widget => widget.Details)
                .WithOne(detail => detail.Widget)
                .HasForeignKey(detail => detail.WidgetId);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class UnitWidget
    {
        public int Id { get; set; }

        public int Category { get; set; }

        public string Name { get; set; } = string.Empty;

        public ICollection<UnitWidgetDetail> Details { get; } = [];
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class UnitWidgetDetail
    {
        public int Id { get; set; }

        public int WidgetId { get; set; }

        public string Description { get; set; } = string.Empty;

        public UnitWidget Widget { get; set; } = null!;
    }
}
