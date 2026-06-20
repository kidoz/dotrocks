using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using DotRocks.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DotRocks.Analyzers.Tests;

public sealed class DotRocksUsageAnalyzerTests
{
    [Fact]
    public async Task InsecureStreamLoadEndpointWithCredentialsConstructor_ReportsDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                DotRocksStubs
                    + """

                    internal static class Sample
                    {
                        public static void Create()
                        {
                            const string connectionString =
                                "Server=127.0.0.1;User ID=root;Password=secret;Stream Load Endpoint=http://127.0.0.1:8030";
                            _ = new DotRocks.Data.Loading.DotRocksStreamLoadClient(connectionString);
                        }
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
    public async Task InsecureConnectionStringBuilderInitializer_ReportsDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                DotRocksStubs
                    + """

                    internal static class Sample
                    {
                        public static void Create()
                        {
                            _ = new DotRocks.Data.DotRocksConnectionStringBuilder
                            {
                                Password = "secret",
                                StreamLoadEndpoint = "http://127.0.0.1:8030",
                            };
                        }
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
    public async Task HttpStreamLoadEndpointWithoutCredentials_DoesNotReportDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                DotRocksStubs
                    + """

                    internal static class Sample
                    {
                        public static void Create()
                        {
                            _ = new DotRocks.Data.DotRocksConnectionStringBuilder
                            {
                                StreamLoadEndpoint = "http://127.0.0.1:8030",
                            };
                        }
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
    public async Task PlainStringLiteralConnectionString_DoesNotReportDiagnosticUntilUsed()
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

        Assert.DoesNotContain(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksUsageAnalyzer.InsecureStreamLoadEndpointDiagnosticId
        );
    }

    [Fact]
    public async Task HttpsStreamLoadEndpointWithCredentials_DoesNotReportDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                DotRocksStubs
                    + """

                    internal static class Sample
                    {
                        public static void Create()
                        {
                            _ = new DotRocks.Data.Loading.DotRocksStreamLoadClient(
                                "Server=127.0.0.1;User ID=root;Password=secret;Stream Load Endpoint=https://127.0.0.1:8030");
                        }
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
                && diagnostic
                    .GetMessage(CultureInfo.InvariantCulture)
                    .Contains("Id", StringComparison.Ordinal)
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

    [Fact]
    public async Task CompositeKeyWithOneMissingValueGeneratedNever_ReportsOnlyMissingProperty()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                EfStubs
                    + """

                    internal sealed class Widget
                    {
                        public int Id { get; set; }
                        public int Category { get; set; }
                    }

                    internal sealed class SampleContext : Microsoft.EntityFrameworkCore.DbContext
                    {
                        protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
                        {
                            modelBuilder.Entity<Widget>().HasKey(widget => new { widget.Id, widget.Category });
                            modelBuilder.Entity<Widget>().Property(widget => widget.Id).ValueGeneratedNever();
                        }
                    }
                    """
            )
            .ConfigureAwait(true);

        Diagnostic diagnostic = Assert.Single(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksUsageAnalyzer.MissingValueGeneratedNeverDiagnosticId
        );
        Assert.Contains(
            "Category",
            diagnostic.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal
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
                DotRocksStubs
                    + """

                    internal static class Sample
                    {
                        public static void Complete(DotRocks.Data.DotRocksTransaction transaction)
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

    [Fact]
    public async Task NonDotRocksTransactionDoubleCompletion_DoesNotReportDiagnostic()
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

        Assert.DoesNotContain(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksUsageAnalyzer.TransactionDoubleCompletionDiagnosticId
        );
    }

    [Fact]
    public async Task AnalyzerPackage_ContainsOnlyAnalyzerAssets()
    {
        string packagePath = await PackAnalyzerAsync().ConfigureAwait(true);

        using ZipArchive archive = await ZipFile
            .OpenReadAsync(packagePath, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.Contains("analyzers/dotnet/cs/DotRocks.Analyzers.dll", entries);
        Assert.DoesNotContain(entries, entry => entry.StartsWith("lib/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.StartsWith("ref/", StringComparison.Ordinal));

        ZipArchiveEntry nuspecEntry = Assert.Single(
            entries.Select(entry => archive.GetEntry(entry)),
            entry => entry?.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) == true
        )!;
        using Stream nuspecStream = await nuspecEntry
            .OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        XDocument nuspec = XDocument.Load(nuspecStream);
        Assert.DoesNotContain(
            nuspec.Descendants(),
            element => element.Name.LocalName == "dependency"
        );
    }

    [Fact]
    public async Task AnalyzerConsumerFixture_ReportsExpectedDiagnostics()
    {
        CommandResult result = await BuildConsumerFixtureAsync("DIAGNOSTIC").ConfigureAwait(true);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            DotRocksUsageAnalyzer.InsecureStreamLoadEndpointDiagnosticId,
            result.Output,
            StringComparison.Ordinal
        );
        Assert.Contains(
            DotRocksUsageAnalyzer.MissingValueGeneratedNeverDiagnosticId,
            result.Output,
            StringComparison.Ordinal
        );
        Assert.Contains(
            DotRocksUsageAnalyzer.UnsupportedBinaryMappingDiagnosticId,
            result.Output,
            StringComparison.Ordinal
        );
        Assert.Contains(
            DotRocksUsageAnalyzer.TransactionDoubleCompletionDiagnosticId,
            result.Output,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task AnalyzerConsumerFixture_CleanCodeReportsNoDiagnostics()
    {
        CommandResult result = await BuildConsumerFixtureAsync("CLEAN").ConfigureAwait(true);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("DTR000", result.Output, StringComparison.Ordinal);
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

    private static async Task<string> PackAnalyzerAsync()
    {
        string root = FindRepositoryRoot();
        string outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "dotrocks-analyzer-pack-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(outputDirectory);
        string configuration = GetAssemblyConfiguration();
        string projectPath = Path.Combine(
            root,
            "src",
            "DotRocks.Analyzers",
            "DotRocks.Analyzers.csproj"
        );
        CommandResult result = await RunDotnetAsync(
                $"pack \"{projectPath}\" --configuration {configuration} --no-build --output \"{outputDirectory}\""
            )
            .ConfigureAwait(true);

        Assert.Equal(0, result.ExitCode);
        return Assert.Single(Directory.GetFiles(outputDirectory, "DotRocks.Analyzers.*.nupkg"));
    }

    private static Task<CommandResult> BuildConsumerFixtureAsync(string mode)
    {
        string root = FindRepositoryRoot();
        string configuration = GetAssemblyConfiguration();
        string projectPath = Path.Combine(
            root,
            "tests",
            "DotRocks.Analyzers.Tests",
            "Fixtures",
            "AnalyzerConsumer",
            "DotRocks.Analyzers.Consumer.csproj"
        );
        return RunDotnetAsync(
            $"build \"{projectPath}\" --configuration {configuration} /p:AnalyzerFixtureMode={mode}"
        );
    }

    private static async Task<CommandResult> RunDotnetAsync(string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(true);
        string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(true);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        return new CommandResult(process.ExitCode, output + error);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotRocks.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DotRocks.slnx.");
    }

    private static string GetAssemblyConfiguration() =>
        typeof(DotRocksUsageAnalyzerTests)
            .Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration
        ?? "Debug";

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

    private const string DotRocksStubs = """
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

    private sealed record CommandResult(int ExitCode, string Output);
}
