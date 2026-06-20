using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksModelValidationTests
{
    [Fact]
    public void GeneratedKey_ThrowsNotSupportedException()
    {
        using var context = CreateContext<GeneratedKeyContext>();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => context.Model);

        Assert.Contains("ValueGeneratedNever", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_ThrowsNotSupportedException()
    {
        using var context = CreateContext<NavigationContext>();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => context.Model);

        Assert.Contains("navigations", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OwnedEntity_ThrowsNotSupportedException()
    {
        using var context = CreateContext<OwnedContext>();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => context.Model);

        Assert.Contains(
            "scalar properties only",
            exception.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public void ConcurrencyToken_ThrowsNotSupportedException()
    {
        using var context = CreateContext<ConcurrencyContext>();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => context.Model);

        Assert.Contains("concurrency token", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BinaryProperty_ThrowsNotSupportedException()
    {
        using var context = CreateContext<BinaryContext>();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => context.Model);

        Assert.Contains("Byte[]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeKey_ThrowsNotSupportedException()
    {
        using var context = CreateContext<CompositeKeyContext>();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => context.Model);

        Assert.Contains("single-column primary key", exception.Message, StringComparison.Ordinal);
    }

    private static TContext CreateContext<TContext>()
        where TContext : DbContext
    {
        var optionsBuilder = new DbContextOptionsBuilder<TContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");
        return (TContext)Activator.CreateInstance(typeof(TContext), optionsBuilder.Options)!;
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through reflection."
    )]
    private sealed class GeneratedKeyContext(DbContextOptions<GeneratedKeyContext> options)
        : DbContext(options)
    {
        public DbSet<GeneratedKeyEntity> Entities => Set<GeneratedKeyEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<GeneratedKeyEntity>().HasKey(entity => entity.Id);
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through reflection."
    )]
    private sealed class NavigationContext(DbContextOptions<NavigationContext> options)
        : DbContext(options)
    {
        public DbSet<NavigationEntity> Entities => Set<NavigationEntity>();

        public DbSet<NavigationDetail> Details => Set<NavigationDetail>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NavigationEntity>().HasKey(entity => entity.Id);
            modelBuilder
                .Entity<NavigationEntity>()
                .Property(entity => entity.Id)
                .ValueGeneratedNever();
            modelBuilder.Entity<NavigationDetail>().HasKey(detail => detail.Id);
            modelBuilder
                .Entity<NavigationDetail>()
                .Property(detail => detail.Id)
                .ValueGeneratedNever();
            modelBuilder
                .Entity<NavigationEntity>()
                .HasMany(entity => entity.Details)
                .WithOne(detail => detail.Entity)
                .HasForeignKey(detail => detail.EntityId);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through reflection."
    )]
    private sealed class OwnedContext(DbContextOptions<OwnedContext> options) : DbContext(options)
    {
        public DbSet<OwnedRoot> Roots => Set<OwnedRoot>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OwnedRoot>().HasKey(root => root.Id);
            modelBuilder.Entity<OwnedRoot>().Property(root => root.Id).ValueGeneratedNever();
            modelBuilder.Entity<OwnedRoot>().OwnsOne(root => root.Owned);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through reflection."
    )]
    private sealed class ConcurrencyContext(DbContextOptions<ConcurrencyContext> options)
        : DbContext(options)
    {
        public DbSet<ConcurrencyEntity> Entities => Set<ConcurrencyEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConcurrencyEntity>().HasKey(entity => entity.Id);
            modelBuilder
                .Entity<ConcurrencyEntity>()
                .Property(entity => entity.Id)
                .ValueGeneratedNever();
            modelBuilder
                .Entity<ConcurrencyEntity>()
                .Property(entity => entity.Version)
                .IsConcurrencyToken();
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through reflection."
    )]
    private sealed class BinaryContext(DbContextOptions<BinaryContext> options) : DbContext(options)
    {
        public DbSet<BinaryEntity> Entities => Set<BinaryEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BinaryEntity>().HasKey(entity => entity.Id);
            modelBuilder.Entity<BinaryEntity>().Property(entity => entity.Id).ValueGeneratedNever();
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through reflection."
    )]
    private sealed class CompositeKeyContext(DbContextOptions<CompositeKeyContext> options)
        : DbContext(options)
    {
        public DbSet<CompositeKeyEntity> Entities => Set<CompositeKeyEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .Entity<CompositeKeyEntity>()
                .HasKey(entity => new { entity.Id, entity.Category });
            modelBuilder
                .Entity<CompositeKeyEntity>()
                .Property(entity => entity.Id)
                .ValueGeneratedNever();
            modelBuilder
                .Entity<CompositeKeyEntity>()
                .Property(entity => entity.Category)
                .ValueGeneratedNever();
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class GeneratedKeyEntity
    {
        public int Id { get; set; }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class NavigationEntity
    {
        public int Id { get; set; }

        public ICollection<NavigationDetail> Details { get; } = [];
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class NavigationDetail
    {
        public int Id { get; set; }

        public int EntityId { get; set; }

        public NavigationEntity Entity { get; set; } = null!;
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class OwnedRoot
    {
        public int Id { get; set; }

        public OwnedValue Owned { get; set; } = new();
    }

    private sealed class OwnedValue
    {
        public string Name { get; set; } = string.Empty;
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class ConcurrencyEntity
    {
        public int Id { get; set; }

        public int Version { get; set; }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class BinaryEntity
    {
        public int Id { get; set; }

        public byte[] Data { get; set; } = [];
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class CompositeKeyEntity
    {
        public int Id { get; set; }

        public int Category { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
