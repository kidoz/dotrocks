using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

public sealed class DotRocksUpdateSqlGeneratorTests
{
    [Fact]
    public void AppendInsertOperation_ProducesParameterizedInsert()
    {
        using var context = CreateContext();
        IUpdateSqlGenerator generator = context.GetService<IUpdateSqlGenerator>();
        IReadOnlyModificationCommand command = CreateCommand(
            context,
            EntityState.Added,
            [
                Column("id", key: true, write: true, condition: false, "p0"),
                Column("name", key: false, write: true, condition: false, "p1"),
            ]
        );

        var builder = new StringBuilder();
        generator.AppendInsertOperation(builder, command, 0, out bool requiresTransaction);

        Assert.Equal(
            "INSERT INTO `widgets` (`id`, `name`) VALUES (@p0, @p1);",
            builder.ToString().Trim(),
            StringComparer.Ordinal
        );
        Assert.False(requiresTransaction);
    }

    [Fact]
    public void AppendUpdateOperation_ProducesSetAndKeyCondition()
    {
        using var context = CreateContext();
        IUpdateSqlGenerator generator = context.GetService<IUpdateSqlGenerator>();
        IReadOnlyModificationCommand command = CreateCommand(
            context,
            EntityState.Modified,
            [
                Column("id", key: true, write: false, condition: true, "p0"),
                Column("name", key: false, write: true, condition: false, "p1"),
            ]
        );

        var builder = new StringBuilder();
        generator.AppendUpdateOperation(builder, command, 0, out _);

        Assert.Equal(
            "UPDATE `widgets` SET `name` = @p1 WHERE `id` = @p0;",
            builder.ToString().Trim(),
            StringComparer.Ordinal
        );
    }

    [Fact]
    public void AppendDeleteOperation_ProducesKeyCondition()
    {
        using var context = CreateContext();
        IUpdateSqlGenerator generator = context.GetService<IUpdateSqlGenerator>();
        IReadOnlyModificationCommand command = CreateCommand(
            context,
            EntityState.Deleted,
            [Column("id", key: true, write: false, condition: true, "p0")]
        );

        var builder = new StringBuilder();
        generator.AppendDeleteOperation(builder, command, 0, out _);

        Assert.Equal(
            "DELETE FROM `widgets` WHERE `id` = @p0;",
            builder.ToString().Trim(),
            StringComparer.Ordinal
        );
    }

    [Fact]
    public void AppendUpdateOperation_WithoutCondition_Throws()
    {
        using var context = CreateContext();
        IUpdateSqlGenerator generator = context.GetService<IUpdateSqlGenerator>();
        IReadOnlyModificationCommand command = CreateCommand(
            context,
            EntityState.Modified,
            [Column("name", key: false, write: true, condition: false, "p1")]
        );

        var builder = new StringBuilder();
        Assert.Throws<NotSupportedException>(() =>
            generator.AppendUpdateOperation(builder, command, 0, out _)
        );
    }

    private static IReadOnlyModificationCommand CreateCommand(
        DbContext context,
        EntityState state,
        ColumnSpec[] columns
    )
    {
        IModificationCommandFactory factory = context.GetService<IModificationCommandFactory>();
        var parameters = new NonTrackedModificationCommandParameters("widgets", null, true);
        INonTrackedModificationCommand command = factory.CreateNonTrackedModificationCommand(
            in parameters
        );
        command.EntityState = state;
        foreach (ColumnSpec column in columns)
        {
            command.AddColumnModification(
                new ColumnModificationParameters(
                    column.Name,
                    originalValue: 1,
                    value: 1,
                    property: null,
                    columnType: "int",
                    typeMapping: null,
                    read: false,
                    write: column.Write,
                    key: column.Key,
                    condition: column.Condition,
                    sensitiveLoggingEnabled: false,
                    isNullable: null
                )
                {
                    GenerateParameterName = () => column.ParameterName,
                }
            );
        }

        return command;
    }

    private static ColumnSpec Column(
        string name,
        bool key,
        bool write,
        bool condition,
        string parameterName
    ) => new(name, key, write, condition, parameterName);

    private static UnitContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<UnitContext>();
        optionsBuilder.UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root");
        return new UnitContext(optionsBuilder.Options);
    }

    private readonly record struct ColumnSpec(
        string Name,
        bool Key,
        bool Write,
        bool Condition,
        string ParameterName
    );

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
            modelBuilder.Entity<UnitWidget>().ToTable("widgets").HasKey(widget => widget.Id);
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
