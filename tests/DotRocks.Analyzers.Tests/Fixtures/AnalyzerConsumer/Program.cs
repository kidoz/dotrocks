using DotRocks.Data;
using DotRocks.Data.Loading;
using Microsoft.EntityFrameworkCore;

#if DIAGNOSTIC
_ = new DotRocksStreamLoadClient(
    "Server=127.0.0.1;User ID=root;Password=secret;Stream Load Endpoint=http://127.0.0.1:8030"
);
#else
_ = new DotRocksStreamLoadClient(
    "Server=127.0.0.1;User ID=root;Password=secret;Stream Load Endpoint=https://127.0.0.1:8030"
);
#endif

namespace DotRocks.Data
{
    public sealed class DotRocksTransaction
    {
        public void Commit() { }

        public void Rollback() { }
    }
}

namespace DotRocks.Data.Loading
{
    public sealed class DotRocksStreamLoadClient
    {
        public DotRocksStreamLoadClient(string connectionString) { }
    }
}

namespace Microsoft.EntityFrameworkCore
{
    public class DbContext
    {
        protected virtual void OnModelCreating(ModelBuilder modelBuilder) { }
    }

    public class ModelBuilder
    {
        public EntityTypeBuilder<TEntity> Entity<TEntity>() => new();
    }

    public class EntityTypeBuilder<TEntity>
    {
        public EntityTypeBuilder<TEntity> HasKey<TProperty>(
            System.Linq.Expressions.Expression<System.Func<TEntity, TProperty>> keyExpression
        ) => this;

        public PropertyBuilder<TProperty> Property<TProperty>(
            System.Linq.Expressions.Expression<System.Func<TEntity, TProperty>> propertyExpression
        ) => new();
    }

    public class PropertyBuilder<TProperty>
    {
        public PropertyBuilder<TProperty> ValueGeneratedNever() => this;

        public PropertyBuilder<TProperty> HasColumnType(string storeType) => this;
    }
}

internal sealed class Widget
{
    public int Id { get; set; }

    public byte[] Data { get; set; } = [];
}

internal sealed class SampleContext : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>().HasKey(widget => widget.Id);
#if CLEAN
        modelBuilder.Entity<Widget>().Property(widget => widget.Id).ValueGeneratedNever();
#else
        modelBuilder.Entity<Widget>().Property(widget => widget.Data).HasColumnType("varbinary");
#endif
    }
}

internal static class TransactionSample
{
    public static void Complete(DotRocksTransaction transaction)
    {
        transaction.Commit();
#if DIAGNOSTIC
        transaction.Rollback();
#endif
    }
}
