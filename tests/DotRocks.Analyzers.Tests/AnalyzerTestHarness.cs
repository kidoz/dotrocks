using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DotRocks.Analyzers.Tests;

/// <summary>
/// Shared compilation, stub-source, and assertion helpers for the analyzer test classes, so the
/// DotRocks and EF Core API stubs exist once and cannot drift between test files.
/// </summary>
internal static class AnalyzerTestHarness
{
    public static async Task<Diagnostic[]> AnalyzeAsync(
        string source,
        params DiagnosticAnalyzer[] analyzers
    )
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "DotRocks.Analyzers.Tests.Target",
            [
                CSharpSyntaxTree.ParseText(
                    source,
                    CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)
                ),
            ],
            CreateReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        CompilationWithAnalyzers compilationWithAnalyzers = compilation.WithAnalyzers([
            .. analyzers,
        ]);
        ImmutableArray<Diagnostic> diagnostics = await compilationWithAnalyzers
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(true);
        return diagnostics.OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal).ToArray();
    }

    public static MetadataReference[] CreateReferences()
    {
        string trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        return trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    public static void AssertHasDiagnostic(Diagnostic[] diagnostics, string id) =>
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == id);

    public static void AssertNoDiagnostic(Diagnostic[] diagnostics, string id) =>
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == id);

    public const string DotRocksStubs = """
        namespace DotRocks.Data
        {
            public sealed class DotRocksConnection
            {
                public DotRocksConnection(string connectionString) { }
            }

            public sealed class DotRocksDataSource
            {
                public DotRocksDataSource(string connectionString) { }
            }

            public sealed class DotRocksConnectionStringBuilder
            {
                public DotRocksConnectionStringBuilder() { }

                public DotRocksConnectionStringBuilder(string connectionString) { }

                public string Password { get; set; } = string.Empty;

                public string StreamLoadEndpoint { get; set; } = string.Empty;
            }

            public sealed class DotRocksCommand
            {
                public DotRocksCommand() { }

                public DotRocksCommand(string commandText) { }

                public string CommandText { get; set; } = string.Empty;

                public System.Threading.Tasks.Task<int> ExecuteNonQueryAsync(
                    System.Threading.CancellationToken cancellationToken = default) =>
                    System.Threading.Tasks.Task.FromResult(0);
            }

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

            public sealed class DotRocksStreamLoadTransaction
            {
                public void CommitAsync() { }

                public void RollbackAsync() { }
            }
        }

        """;

    public const string EfStubs = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext
            {
                public Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database { get; } = new();

                public int SaveChanges() => 0;

                public System.Threading.Tasks.Task<int> SaveChangesAsync() => System.Threading.Tasks.Task.FromResult(0);

                public void AddRange(params object[] entities) { }

                public void UpdateRange(params object[] entities) { }

                public void RemoveRange(params object[] entities) { }

                protected virtual void OnModelCreating(ModelBuilder modelBuilder) { }
            }

            public class ModelBuilder
            {
                public Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> Entity<TEntity>() => new();
            }

            public class DbSet<TEntity>
            {
                public void AddRange(params TEntity[] entities) { }

                public void UpdateRange(params TEntity[] entities) { }

                public void RemoveRange(params TEntity[] entities) { }
            }

            public static class EntityFrameworkQueryableExtensions
            {
                public static int ExecuteUpdate<TEntity>(
                    this System.Linq.IQueryable<TEntity> source
                ) => 0;

                public static System.Threading.Tasks.Task<int> ExecuteUpdateAsync<TEntity>(
                    this System.Linq.IQueryable<TEntity> source
                ) => System.Threading.Tasks.Task.FromResult(0);

                public static int ExecuteDelete<TEntity>(
                    this System.Linq.IQueryable<TEntity> source
                ) => 0;

                public static System.Threading.Tasks.Task<int> ExecuteDeleteAsync<TEntity>(
                    this System.Linq.IQueryable<TEntity> source
                ) => System.Threading.Tasks.Task.FromResult(0);
            }
        }

        namespace Microsoft.EntityFrameworkCore.Metadata.Builders
        {
            public class EntityTypeBuilder<TEntity>
            {
                public EntityTypeBuilder<TEntity> HasKey<TProperty>(System.Linq.Expressions.Expression<System.Func<TEntity, TProperty>> keyExpression) => this;

                public PropertyBuilder<TProperty> Property<TProperty>(System.Linq.Expressions.Expression<System.Func<TEntity, TProperty>> propertyExpression) => new();
            }

            public class PropertyBuilder<TProperty>
            {
                public PropertyBuilder<TProperty> ValueGeneratedNever() => this;

                public PropertyBuilder<TProperty> HasColumnType(string storeType) => this;
            }
        }

        namespace Microsoft.EntityFrameworkCore.Infrastructure
        {
            public sealed class DatabaseFacade
            {
                public bool EnsureCreated() => true;

                public System.Threading.Tasks.Task<bool> EnsureCreatedAsync() =>
                    System.Threading.Tasks.Task.FromResult(true);

                public bool EnsureDeleted() => true;

                public System.Threading.Tasks.Task<bool> EnsureDeletedAsync() =>
                    System.Threading.Tasks.Task.FromResult(true);
            }
        }
        """;
}
