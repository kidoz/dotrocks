using System.Diagnostics.CodeAnalysis;
using DotRocks.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

string connectionString =
    Environment.GetEnvironmentVariable("DOTROCKS_CONNECTION_STRING")
    ?? "Server=127.0.0.1;Port=9030;User ID=root;Database=dotrocks_sample";

var optionsBuilder = new DbContextOptionsBuilder<SampleContext>();
optionsBuilder.UseStarRocks(
    connectionString,
    // Pin the target StarRocks version. Building the options never contacts the server;
    // use StarRocksServerVersion.DetectAsync(connectionString) to discover it once at startup.
    starRocks => starRocks.ServerVersion(new StarRocksServerVersion(4, 0, 7))
);
DbContextOptions<SampleContext> options = optionsBuilder.Options;

using var context = new SampleContext(options);

await context.Database.MigrateAsync().ConfigureAwait(false);

var widget = new Widget
{
    Id = 1,
    Name = "inserted",
    Active = true,
    Amount = 12.34m,
};

context.Widgets.Add(widget);
await context.SaveChangesAsync().ConfigureAwait(false);

widget.Name = "updated";
widget.Active = false;
widget.Amount = 56.78m;
await context.SaveChangesAsync().ConfigureAwait(false);

context.Widgets.Remove(widget);
await context.SaveChangesAsync().ConfigureAwait(false);

internal sealed class SampleContext(DbContextOptions<SampleContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>(entity =>
        {
            entity.ToTable("ef_sample_widgets");
            entity.HasKey(widget => widget.Id);
            entity.Property(widget => widget.Id).ValueGeneratedNever();
            entity.Property(widget => widget.Name).ValueGeneratedNever().HasMaxLength(64);
            entity.Property(widget => widget.Active).ValueGeneratedNever();
            entity
                .Property(widget => widget.Amount)
                .ValueGeneratedNever()
                .HasColumnType("decimal(10, 2)");
            entity.HasStarRocksPrimaryKey("id");
            entity.HasStarRocksHashDistribution(1, "id");
            entity.HasStarRocksReplicationNum(1);
        });
    }
}

internal sealed class Widget
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Active { get; set; }

    public decimal Amount { get; set; }
}

[Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(SampleContext))]
[Migration("202606190001_CreateEfSampleWidgets")]
[SuppressMessage(
    "Design",
    "CA1034:Nested types should not be visible",
    Justification = "The sample keeps its minimal migration in the same source file for readability."
)]
internal sealed class CreateEfSampleWidgets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ef_sample_widgets",
            columns: table => new
            {
                Id = table.Column<int>(name: "id", nullable: false),
                Name = table.Column<string>(name: "name", type: "varchar(64)", nullable: false),
                Active = table.Column<bool>(name: "active", nullable: false),
                Amount = table.Column<decimal>(
                    name: "amount",
                    type: "decimal(10, 2)",
                    nullable: false
                ),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ef_sample_widgets", row => row.Id);
            }
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ef_sample_widgets");
    }
}
