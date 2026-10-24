using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksWritableTableValidationTests
{
    [Fact]
    public void WritableTable_RequiresExplicitPrimaryKeyModel()
    {
        using var context = new MissingKeyModelContext(CreateOptions<MissingKeyModelContext>());

        Assert.Contains(
            "PRIMARY KEY table",
            Assert.Throws<NotSupportedException>(() => context.Model).Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void WritableTable_RejectsDuplicateKeyModel()
    {
        using var context = new DuplicateKeyContext(CreateOptions<DuplicateKeyContext>());

        Assert.Contains(
            "PRIMARY KEY table",
            Assert.Throws<NotSupportedException>(() => context.Model).Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void WritableTable_RejectsUniqueKeyModel()
    {
        using var context = new UniqueKeyContext(CreateOptions<UniqueKeyContext>());

        Assert.Throws<NotSupportedException>(() => context.Model);
    }

    [Fact]
    public void WritableTable_RequiresMatchingStoreKeyColumns()
    {
        using var context = new MismatchedKeyContext(CreateOptions<MismatchedKeyContext>());

        Assert.Contains(
            "matching",
            Assert.Throws<NotSupportedException>(() => context.Model).Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void WritableTable_RequiresMatchingCompositeKeyOrder()
    {
        using var context = new ReorderedKeyContext(CreateOptions<ReorderedKeyContext>());

        Assert.Contains(
            "same order",
            Assert.Throws<NotSupportedException>(() => context.Model).Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void WritableTable_AcceptsMatchingRenamedCompositeStoreColumns()
    {
        using var context = new MatchingKeyContext(CreateOptions<MatchingKeyContext>());

        Assert.NotNull(context.Model.FindEntityType(typeof(Row)));
    }

    [Fact]
    public void KeylessTable_AllowsDuplicateKeyModel()
    {
        using var context = new KeylessContext(CreateOptions<KeylessContext>());

        Assert.Null(context.Model.FindEntityType(typeof(Row))!.FindPrimaryKey());
    }

    private static DbContextOptions<TContext> CreateOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseStarRocks("Server=127.0.0.1;User ID=root")
            .Options;

    private abstract class ShapeContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual bool IsKeyless => false;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Row>(entity =>
            {
                entity.ToTable("rows");
                if (!IsKeyless)
                {
                    entity.HasKey(row => new { row.Id, row.Category });
                }
                entity.Property(row => row.Id).ValueGeneratedNever().HasColumnName("row_id");
                entity
                    .Property(row => row.Category)
                    .ValueGeneratedNever()
                    .HasColumnName("category");
                ConfigureKey(entity);
            });
        }

        protected abstract void ConfigureKey(EntityTypeBuilder<Row> entity);
    }

    private sealed class MissingKeyModelContext(DbContextOptions<MissingKeyModelContext> options)
        : ShapeContext(options)
    {
        protected override void ConfigureKey(EntityTypeBuilder<Row> entity) { }
    }

    private sealed class DuplicateKeyContext(DbContextOptions<DuplicateKeyContext> options)
        : ShapeContext(options)
    {
        protected override void ConfigureKey(EntityTypeBuilder<Row> entity) =>
            entity.HasStarRocksDuplicateKey("row_id", "category");
    }

    private sealed class UniqueKeyContext(DbContextOptions<UniqueKeyContext> options)
        : ShapeContext(options)
    {
        protected override void ConfigureKey(EntityTypeBuilder<Row> entity) =>
            entity.HasStarRocksUniqueKey("row_id", "category");
    }

    private sealed class MismatchedKeyContext(DbContextOptions<MismatchedKeyContext> options)
        : ShapeContext(options)
    {
        protected override void ConfigureKey(EntityTypeBuilder<Row> entity) =>
            entity.HasStarRocksPrimaryKey("row_id");
    }

    private sealed class ReorderedKeyContext(DbContextOptions<ReorderedKeyContext> options)
        : ShapeContext(options)
    {
        protected override void ConfigureKey(EntityTypeBuilder<Row> entity) =>
            entity.HasStarRocksPrimaryKey("category", "row_id");
    }

    private sealed class MatchingKeyContext(DbContextOptions<MatchingKeyContext> options)
        : ShapeContext(options)
    {
        protected override void ConfigureKey(EntityTypeBuilder<Row> entity) =>
            entity.HasStarRocksPrimaryKey("row_id", "category");
    }

    private sealed class KeylessContext(DbContextOptions<KeylessContext> options)
        : ShapeContext(options)
    {
        protected override bool IsKeyless => true;

        protected override void ConfigureKey(EntityTypeBuilder<Row> entity)
        {
            entity.HasNoKey();
            entity.HasStarRocksDuplicateKey("row_id");
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through model metadata."
    )]
    private sealed class Row
    {
        public int Id { get; set; }

        public int Category { get; set; }
    }
}
