using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DotRocks.EntityFrameworkCore.Tests;

/// <summary>
/// Pins the StarRocks shape of <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> translation without a
/// server: the target table is emitted without an alias, its columns unqualified, captured
/// values as parameters, and unsupported query shapes fail before any SQL is sent. Live
/// execution is covered by the EF Core integration suite.
/// </summary>
public sealed class DotRocksExecuteUpdateDeleteTests
{
    [Fact]
    public async Task ExecuteDeleteAsync_WithPredicate_EmitsUnaliasedDeleteWithParameter()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);
        int id = 7;

        int affected = await context
            .Widgets.Where(widget => widget.Id == id)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DmlCapture.SuppressedResult, affected);
        Assert.Equal("DELETE FROM `unit_db`.`widgets`\nWHERE `id` = @id", capture.CommandText);
        Assert.Equal(7, Assert.Single(capture.ParameterValues));
    }

    [Fact]
    public void ExecuteDelete_Synchronous_EmitsSameShape()
    {
        var capture = new DmlCapture();
        using UnitContext context = CreateContext(capture);

        int affected = context.Widgets.Where(widget => widget.Id == 7).ExecuteDelete();

        Assert.Equal(DmlCapture.SuppressedResult, affected);
        Assert.Equal("DELETE FROM `unit_db`.`widgets`\nWHERE `id` = 7", capture.CommandText);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_WithoutPredicate_EmitsWhereTrue()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);

        await context.Widgets.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        // StarRocks refuses DELETE without WHERE; `WHERE TRUE` is its documented "all rows".
        Assert.Equal("DELETE FROM `unit_db`.`widgets`\nWHERE TRUE", capture.CommandText);
        Assert.Empty(capture.ParameterValues);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_WithCompoundPredicate_KeepsColumnsUnqualified()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);
        string prefix = "old";

        await context
            .Widgets.Where(widget => widget.Active && widget.Name.StartsWith(prefix))
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capture.CommandText);
        Assert.StartsWith(
            "DELETE FROM `unit_db`.`widgets`\nWHERE `active`",
            capture.CommandText,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(" AS ", capture.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("`w`.", capture.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("'old", capture.CommandText, StringComparison.Ordinal);
        Assert.Contains("old", capture.ParameterValues);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_WithContainsOverCollection_EmitsInList()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);
        int[] ids = [1, 2, 3];

        await context
            .Widgets.Where(widget => ids.Contains(widget.Id))
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capture.CommandText);
        Assert.Contains("WHERE `id` IN (", capture.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", capture.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteUpdateAsync_WithParameterSetter_EmitsUnaliasedUpdate()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);
        string name = "renamed";
        int id = 7;

        int affected = await context
            .Widgets.Where(widget => widget.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(widget => widget.Name, name),
                TestContext.Current.CancellationToken
            );

        Assert.Equal(DmlCapture.SuppressedResult, affected);
        // EF parameterizes SetProperty values (`@p`) even when they are constants, so no
        // caller value is ever inlined into the statement text.
        Assert.Equal(
            "UPDATE `unit_db`.`widgets`\nSET `name` = @p\nWHERE `id` = @id",
            capture.CommandText
        );
        Assert.Equal(["renamed", 7], capture.ParameterValues);
    }

    [Fact]
    public async Task ExecuteUpdateAsync_WithColumnExpressionSetter_ReferencesOwnColumnsBare()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);

        await context
            .Widgets.Where(widget => widget.Active)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(widget => widget.Count, widget => widget.Count + 1)
                        .SetProperty(widget => widget.Active, false),
                TestContext.Current.CancellationToken
            );

        Assert.NotNull(capture.CommandText);
        Assert.StartsWith(
            "UPDATE `unit_db`.`widgets`\nSET ",
            capture.CommandText,
            StringComparison.Ordinal
        );
        Assert.Contains("`count` = `count` + 1", capture.CommandText, StringComparison.Ordinal);
        Assert.Contains("`active` = @p", capture.CommandText, StringComparison.Ordinal);
        Assert.Contains(false, capture.ParameterValues);
        Assert.EndsWith("\nWHERE `active`", capture.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain(" AS ", capture.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("`w`.", capture.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteUpdateAsync_WithoutPredicate_EmitsWhereTrue()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);

        await context.Widgets.ExecuteUpdateAsync(
            setters => setters.SetProperty(widget => widget.Active, true),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            "UPDATE `unit_db`.`widgets`\nSET `active` = @p\nWHERE TRUE",
            capture.CommandText
        );
    }

    [Fact]
    public async Task ExecuteDeleteAsync_AfterOrderByTake_FailsExplicitlyBeforeSendingSql()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);

        // EF rewrites this shape as `WHERE id IN (SELECT ... ORDER BY ... LIMIT 1)`; StarRocks
        // DELETE has no LIMIT and the rewritten subquery is refused rather than guessed at.
        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            context
                .Widgets.OrderBy(widget => widget.Id)
                .Take(1)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("ExecuteDelete", exception.Message, StringComparison.Ordinal);
        Assert.Contains("subqueries", exception.Message, StringComparison.Ordinal);
        Assert.Null(capture.CommandText);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_WithDistinct_FailsExplicitlyBeforeSendingSql()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            context.Widgets.Distinct().ExecuteDeleteAsync(TestContext.Current.CancellationToken)
        );

        Assert.Null(capture.CommandText);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_WithCorrelatedSubquery_FailsExplicitlyBeforeSendingSql()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);

        // A correlated subquery would need the outer column qualified, which StarRocks DML
        // cannot express; an unqualified `id` could silently bind to the inner table instead.
        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            context
                .Widgets.Where(widget =>
                    context.Categories.Any(category => category.Id == widget.CategoryId)
                )
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("ExecuteDelete", exception.Message, StringComparison.Ordinal);
        Assert.Null(capture.CommandText);
    }

    [Fact]
    public async Task ExecuteUpdateAsync_WithSubquerySetter_FailsExplicitlyBeforeSendingSql()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            context
                .Widgets.Where(widget => widget.Id == 1)
                .ExecuteUpdateAsync(
                    setters =>
                        setters.SetProperty(
                            widget => widget.Count,
                            widget =>
                                context.Categories.Count(category =>
                                    category.Id == widget.CategoryId
                                )
                        ),
                    TestContext.Current.CancellationToken
                )
        );

        Assert.Contains("ExecuteUpdate", exception.Message, StringComparison.Ordinal);
        Assert.Null(capture.CommandText);
    }

    [Fact]
    public async Task ExecuteUpdateAsync_AfterJoin_FailsExplicitlyBeforeSendingSql()
    {
        var capture = new DmlCapture();
        await using UnitContext context = CreateContext(capture);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            context
                .Widgets.Join(
                    context.Categories,
                    widget => widget.CategoryId,
                    category => category.Id,
                    (widget, category) => widget
                )
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(widget => widget.Active, false),
                    TestContext.Current.CancellationToken
                )
        );

        Assert.Null(capture.CommandText);
    }

    private static UnitContext CreateContext(DmlCapture capture)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UnitContext>();
        optionsBuilder
            .UseStarRocks("Server=127.0.0.1;Port=9030;User ID=root")
            .AddInterceptors(capture, new SuppressedConnectionInterceptor());
        return new UnitContext(optionsBuilder.Options);
    }

    /// <summary>
    /// Records the DML command EF hands to the provider and suppresses its execution, so the
    /// generated SQL and parameters can be asserted without a StarRocks server.
    /// </summary>
    private sealed class DmlCapture : DbCommandInterceptor
    {
        public const int SuppressedResult = 42;

        public string? CommandText { get; private set; }

        public object?[] ParameterValues { get; private set; } = [];

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result
        )
        {
            Capture(command);
            return InterceptionResult<int>.SuppressWithResult(SuppressedResult);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            Capture(command);
            return ValueTask.FromResult(
                InterceptionResult<int>.SuppressWithResult(SuppressedResult)
            );
        }

        private void Capture(DbCommand command)
        {
            CommandText = command.CommandText.Replace("\r\n", "\n", StringComparison.Ordinal);
            ParameterValues = command
                .Parameters.Cast<DbParameter>()
                .Select(parameter => parameter.Value)
                .ToArray();
        }
    }

    /// <summary>
    /// Keeps EF from opening the (absent) StarRocks connection before the suppressed command.
    /// </summary>
    private sealed class SuppressedConnectionInterceptor : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result
        ) => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(InterceptionResult.Suppress());
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The test methods instantiate this nested context through its primary constructor."
    )]
    private sealed class UnitContext(DbContextOptions<UnitContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        public DbSet<Category> Categories => Set<Category>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().ToTable("widgets", "unit_db").HasKey(widget => widget.Id);
            modelBuilder
                .Entity<Widget>()
                .Property(widget => widget.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            modelBuilder
                .Entity<Widget>()
                .Property(widget => widget.CategoryId)
                .HasColumnName("category_id");
            modelBuilder.Entity<Widget>().Property(widget => widget.Name).HasColumnName("name");
            modelBuilder.Entity<Widget>().Property(widget => widget.Active).HasColumnName("active");
            modelBuilder.Entity<Widget>().Property(widget => widget.Count).HasColumnName("count");
            modelBuilder
                .Entity<Category>()
                .ToTable("categories", "unit_db")
                .HasKey(category => category.Id);
            modelBuilder.Entity<Category>().Property(category => category.Id).ValueGeneratedNever();
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

        public int CategoryId { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool Active { get; set; }

        public int Count { get; set; }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "EF Core uses this entity type through DbSet metadata."
    )]
    private sealed class Category
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
