using System.Collections.Immutable;
using DotRocks.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DotRocks.Analyzers.Tests;

public sealed class DotRocksUsageAnalyzerTests
{
    [Fact]
    public async Task InsecureStreamLoadEndpointWithCredentials_ReportsDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public const string ConnectionString =
                        "Server=127.0.0.1;User ID=root;Password=secret;Stream Load Endpoint=http://127.0.0.1:8030";
                }
                """
            )
            .ConfigureAwait(true);

        Assert.Contains(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksUsageAnalyzer.InsecureStreamLoadEndpointDiagnosticId
        );
    }

    [Fact]
    public async Task HttpsStreamLoadEndpointWithCredentials_DoesNotReportDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public const string ConnectionString =
                        "Server=127.0.0.1;User ID=root;Password=secret;Stream Load Endpoint=https://127.0.0.1:8030";
                }
                """
            )
            .ConfigureAwait(true);

        Assert.DoesNotContain(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksUsageAnalyzer.InsecureStreamLoadEndpointDiagnosticId
        );
    }

    [Fact]
    public async Task EfEntityWithKeyAndNoValueGeneratedNever_ReportsDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                EfStubs
                    + """

                    internal sealed class Widget
                    {
                        public int Id { get; set; }
                    }

                    internal sealed class SampleContext : Microsoft.EntityFrameworkCore.DbContext
                    {
                        protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
                        {
                            modelBuilder.Entity<Widget>().HasKey(widget => widget.Id);
                        }
                    }
                    """
            )
            .ConfigureAwait(true);

        Assert.Contains(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksUsageAnalyzer.MissingValueGeneratedNeverDiagnosticId
        );
    }

    [Fact]
    public async Task EfEntityWithValueGeneratedNever_DoesNotReportDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                EfStubs
                    + """

                    internal sealed class Widget
                    {
                        public int Id { get; set; }
                    }

                    internal sealed class SampleContext : Microsoft.EntityFrameworkCore.DbContext
                    {
                        protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
                        {
                            modelBuilder.Entity<Widget>().HasKey(widget => widget.Id);
                            modelBuilder.Entity<Widget>().Property(widget => widget.Id).ValueGeneratedNever();
                        }
                    }
                    """
            )
            .ConfigureAwait(true);

        Assert.DoesNotContain(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksUsageAnalyzer.MissingValueGeneratedNeverDiagnosticId
        );
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("varbinary")]
    [InlineData("varbinary(256)")]
    public async Task UnsupportedEfBinaryMapping_ReportsDiagnostic(string storeType)
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                EfStubs
                    + $$"""

                    internal sealed class Widget
                    {
                        public int Id { get; set; }
                        public byte[] Data { get; set; } = [];
                    }

                    internal sealed class SampleContext : Microsoft.EntityFrameworkCore.DbContext
                    {
                        protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
                        {
                            modelBuilder.Entity<Widget>().Property(widget => widget.Data).HasColumnType("{{storeType}}");
                        }
                    }
                    """
            )
            .ConfigureAwait(true);

        Assert.Contains(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksUsageAnalyzer.UnsupportedBinaryMappingDiagnosticId
        );
    }

    [Fact]
    public async Task VisibleTransactionDoubleCompletion_ReportsDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal sealed class Transaction
                {
                    public void Commit() { }
                    public void Rollback() { }
                }

                internal static class Sample
                {
                    public static void Complete(Transaction transaction)
                    {
                        transaction.Commit();
                        transaction.Rollback();
                    }
                }
                """
            )
            .ConfigureAwait(true);

        Assert.Contains(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksUsageAnalyzer.TransactionDoubleCompletionDiagnosticId
        );
    }

    private static async Task<Diagnostic[]> AnalyzeAsync(string source)
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

        var analyzer = new DotRocksUsageAnalyzer();
        CompilationWithAnalyzers compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(analyzer)
        );
        ImmutableArray<Diagnostic> diagnostics = await compilationWithAnalyzers
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(true);
        return diagnostics.OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal).ToArray();
    }

    private static MetadataReference[] CreateReferences()
    {
        string trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        return trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private const string EfStubs = """
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
                public EntityTypeBuilder<TEntity> HasKey<TProperty>(System.Linq.Expressions.Expression<System.Func<TEntity, TProperty>> keyExpression) => this;

                public PropertyBuilder<TProperty> Property<TProperty>(System.Linq.Expressions.Expression<System.Func<TEntity, TProperty>> propertyExpression) => new();
            }

            public class PropertyBuilder<TProperty>
            {
                public PropertyBuilder<TProperty> ValueGeneratedNever() => this;

                public PropertyBuilder<TProperty> HasColumnType(string storeType) => this;
            }
        }
        """;
}
