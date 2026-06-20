using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksSqlGenerationTests
{
    [Fact]
    public void SqlGenerationHelper_UsesBacktickIdentifierEscaping()
    {
        using var context = CreateContext();
        var helper = Assert.IsAssignableFrom<RelationalSqlGenerationHelper>(
            context.GetService<ISqlGenerationHelper>()
        );

        Assert.Equal("db``name", helper.EscapeIdentifier("db`name"));
        Assert.Equal("`db``name`", helper.DelimitIdentifier("db`name"));
    }

    [Fact]
    public void SqlGenerationHelper_KeepsAtParameterPlaceholders()
    {
        using var context = CreateContext();
        var helper = context.GetService<ISqlGenerationHelper>();

        Assert.Equal("@p0", helper.GenerateParameterNamePlaceholder("p0"));
    }

    [Fact]
    public void ToQueryString_FormatsSchemaAndTableWithBackticks()
    {
        using var context = CreateContext();

        string sql = context.Widgets.ToQueryString();

        Assert.Contains("FROM `unit_db`.`widgets`", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ToQueryString_UsesLimitOffsetSyntax()
    {
        using var context = CreateContext();

        string sql = context.Widgets.OrderBy(widget => widget.Id).Skip(2).Take(3).ToQueryString();

        Assert.Contains("LIMIT @p", sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET @p", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FETCH FIRST", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToQueryString_SkipWithoutTake_SynthesizesLimitBeforeOffset()
    {
        using var context = CreateContext();

        string sql = context.Widgets.OrderBy(widget => widget.Id).Skip(5).ToQueryString();

        // StarRocks rejects a bare OFFSET; a LIMIT must precede it.
        int limitIndex = sql.IndexOf("LIMIT", StringComparison.Ordinal);
        int offsetIndex = sql.IndexOf("OFFSET", StringComparison.Ordinal);
        Assert.True(limitIndex >= 0, "Expected a synthesized LIMIT for a Skip-only query.");
        Assert.True(offsetIndex >= 0);
        Assert.True(limitIndex < offsetIndex, "LIMIT must precede OFFSET for StarRocks.");
        Assert.Contains(
            long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sql,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void SqlGenerationHelper_FormatsSchemaQualifiedIdentifiers()
    {
        using var context = CreateContext();
        var helper = context.GetService<ISqlGenerationHelper>();

        Assert.Equal("`db`.`table`", helper.DelimitIdentifier("table", "db"));
    }

    [Fact]
    public void ToQueryString_EscapesConstantStringContainsLikeWildcards()
    {
        using var context = CreateContext();

        string sql = context.Widgets.Where(widget => widget.Name.Contains("_%\\")).ToQueryString();

        Assert.Contains("LIKE", sql, StringComparison.OrdinalIgnoreCase);
        // StarRocks rejects the ESCAPE clause and uses backslash as the default LIKE escape.
        Assert.DoesNotContain("ESCAPE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\_", sql, StringComparison.Ordinal);
        Assert.Contains("\\%", sql, StringComparison.Ordinal);
        Assert.Contains("\\\\", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ToQueryString_EscapesParameterizedStringStartsWithLikeWildcards()
    {
        using var context = CreateContext();
        string prefix = "_%\\";

        string sql = context
            .Widgets.Where(widget => widget.Name.StartsWith(prefix))
            .ToQueryString();

        Assert.Contains("@prefix", sql, StringComparison.Ordinal);
        Assert.Contains("REPLACE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ESCAPE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LIKE CONCAT(@prefix", sql, StringComparison.OrdinalIgnoreCase);
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
        public DbSet<UnitWidget> Widgets => Set<UnitWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .Entity<UnitWidget>()
                .ToTable("widgets", "unit_db")
                .HasKey(widget => widget.Id);
            modelBuilder.Entity<UnitWidget>().Property(widget => widget.Id).ValueGeneratedNever();
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

        public string Name { get; set; } = string.Empty;
    }
}
