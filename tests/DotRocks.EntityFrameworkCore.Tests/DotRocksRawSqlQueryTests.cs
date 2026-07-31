using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

/// <summary>
/// Pins the EF-standard raw-SQL entry points against the DotRocks provider: interpolated
/// <c>FromSql</c>/<c>SqlQuery</c> and positional <c>FromSqlRaw</c> placeholders must
/// parameterize automatically instead of requiring hand-built parameter objects. Live execution
/// is exercised by the EF Core integration suite.
/// </summary>
public sealed class DotRocksRawSqlQueryTests
{
    [Fact]
    public void FromSqlInterpolated_ParameterizesCapturedValues()
    {
        using var context = CreateContext();
        int id = 42;
        string name = "alpha";

        string sql = StripParameterPreamble(
            context
                .Widgets.FromSql($"SELECT * FROM widgets WHERE id = {id} AND name = {name}")
                .ToQueryString()
        );

        Assert.Contains("@p0", sql, StringComparison.Ordinal);
        Assert.Contains("@p1", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("id = 42", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'alpha'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void FromSqlRaw_WithPositionalPlaceholders_ParameterizesValues()
    {
        using var context = CreateContext();

        string sql = StripParameterPreamble(
            context
                .Widgets.FromSqlRaw(
                    "SELECT * FROM widgets WHERE id = {0} AND name = {1}",
                    42,
                    "alpha"
                )
                .ToQueryString()
        );

        Assert.Contains("@p0", sql, StringComparison.Ordinal);
        Assert.Contains("@p1", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("id = 42", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlQueryInterpolated_ParameterizesCapturedValues()
    {
        using var context = CreateContext();
        int addend = 41;

        string sql = StripParameterPreamble(
            context.Database.SqlQuery<int>($"SELECT {addend} + 1 AS Value").ToQueryString()
        );

        Assert.Contains("@p0", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("41", sql, StringComparison.Ordinal);
    }

    // ToQueryString prints parameter values in a leading "-- ..." comment block; strip it so
    // absence assertions prove the values left the SQL text itself.
    private static string StripParameterPreamble(string sql) =>
        string.Join(
            Environment.NewLine,
            sql.Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith("--", StringComparison.Ordinal))
        );

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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().ToTable("widgets", "unit_db").HasKey(widget => widget.Id);
            modelBuilder.Entity<Widget>().Property(widget => widget.Id).ValueGeneratedNever();
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

        public string Name { get; set; } = string.Empty;
    }
}
