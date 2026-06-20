using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using DotRocks.Analyzers.CodeFixes;
using DotRocks.Analyzers.Driver;
using DotRocks.Analyzers.EntityFrameworkCore;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
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
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpointDiagnosticId
        );
    }

    [Fact]
    public async Task InsecureStreamLoadEndpointLocalVar_ReportsDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                DotRocksStubs
                    + """

                    internal static class Sample
                    {
                        public static void Create()
                        {
                            var connectionString =
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
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpointDiagnosticId
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
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpointDiagnosticId
        );
    }

    [Fact]
    public async Task InsecureConnectionStringBuilderConnectionStringFlow_ReportsDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                DotRocksStubs
                    + """

                    internal static class Sample
                    {
                        public static void Create()
                        {
                            var builder = new DotRocks.Data.DotRocksConnectionStringBuilder
                            {
                                Password = "secret",
                                StreamLoadEndpoint = "http://127.0.0.1:8030",
                            };
                            _ = new DotRocks.Data.Loading.DotRocksStreamLoadClient(builder.ConnectionString);
                        }
                    }
                    """
            )
            .ConfigureAwait(true);

        Assert.Contains(
            diagnostics,
            diagnostic =>
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpointDiagnosticId
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
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpointDiagnosticId
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
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpointDiagnosticId
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
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpointDiagnosticId
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
                diagnostic.Id
                    == DotRocksDiagnosticDescriptors.MissingValueGeneratedNeverDiagnosticId
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
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.MissingValueGeneratedNeverDiagnosticId
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
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.MissingValueGeneratedNeverDiagnosticId
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
                diagnostic.Id == DotRocksDiagnosticDescriptors.UnsupportedBinaryMappingDiagnosticId
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
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.TransactionDoubleCompletionDiagnosticId
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
                diagnostic.Id
                == DotRocksDiagnosticDescriptors.TransactionDoubleCompletionDiagnosticId
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
            DotRocksDiagnosticDescriptors.InsecureStreamLoadEndpointDiagnosticId,
            result.Output,
            StringComparison.Ordinal
        );
        Assert.Contains(
            DotRocksDiagnosticDescriptors.MissingValueGeneratedNeverDiagnosticId,
            result.Output,
            StringComparison.Ordinal
        );
        Assert.Contains(
            DotRocksDiagnosticDescriptors.UnsupportedBinaryMappingDiagnosticId,
            result.Output,
            StringComparison.Ordinal
        );
        Assert.Contains(
            DotRocksDiagnosticDescriptors.TransactionDoubleCompletionDiagnosticId,
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

    [Fact]
    public async Task InsecureStreamLoadEndpointCodeFix_UsesHttps()
    {
        const string source = """
            namespace DotRocks.Data.Loading
            {
                public sealed class DotRocksStreamLoadClient
                {
                    public DotRocksStreamLoadClient(string connectionString) { }
                }
            }

            internal static class Sample
            {
                public static void Create()
                {
                    _ = new DotRocks.Data.Loading.DotRocksStreamLoadClient(
                        "Server=127.0.0.1;User ID=root;Password=secret;Stream Load Endpoint=http://127.0.0.1:8030");
                }
            }
            """;

        string fixedSource = await ApplyFirstCodeFixAsync(
                source,
                new InsecureStreamLoadEndpointAnalyzer(),
                new InsecureStreamLoadEndpointCodeFixProvider()
            )
            .ConfigureAwait(true);

        Assert.Contains(
            "Stream Load Endpoint=https://127.0.0.1:8030",
            fixedSource,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task EfValueGeneratedNeverCodeFix_AddsPropertyConfiguration()
    {
        string source =
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
                """;

        string fixedSource = await ApplyFirstCodeFixAsync(
                source,
                new EfValueGeneratedNeverAnalyzer(),
                new EfValueGeneratedNeverCodeFixProvider()
            )
            .ConfigureAwait(true);

        Assert.Contains(
            "modelBuilder.Entity<Widget>().Property(widget => widget.Id).ValueGeneratedNever();",
            fixedSource,
            StringComparison.Ordinal
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

        DiagnosticAnalyzer[] analyzers =
        [
            new InsecureStreamLoadEndpointAnalyzer(),
            new EfValueGeneratedNeverAnalyzer(),
            new UnsupportedBinaryMappingAnalyzer(),
            new TransactionCompletionAnalyzer(),
        ];
        CompilationWithAnalyzers compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create(analyzers)
        );
        ImmutableArray<Diagnostic> diagnostics = await compilationWithAnalyzers
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(true);
        return diagnostics.OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal).ToArray();
    }

    private static async Task<string> ApplyFirstCodeFixAsync(
        string source,
        DiagnosticAnalyzer analyzer,
        CodeFixProvider codeFixProvider
    )
    {
        using AdhocWorkspace workspace = new();
        Project project = workspace
            .AddProject("CodeFixTarget", LanguageNames.CSharp)
            .WithCompilationOptions(
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            )
            .WithParseOptions(
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)
            );
        foreach (MetadataReference reference in CreateReferences())
        {
            project = project.AddMetadataReference(reference);
        }

        Document document = project.AddDocument("Target.cs", SourceText.From(source));
        Compilation? compilation = await document
            .Project.GetCompilationAsync()
            .ConfigureAwait(true);
        Assert.NotNull(compilation);
        ImmutableArray<Diagnostic> diagnostics = await compilation!
            .WithAnalyzers(ImmutableArray.Create(analyzer))
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(true);
        Diagnostic diagnostic = Assert.Single(diagnostics);

        List<CodeAction> actions = [];
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            TestContext.Current.CancellationToken
        );
        await codeFixProvider.RegisterCodeFixesAsync(context).ConfigureAwait(true);
        CodeAction codeAction = Assert.Single(actions);
        ImmutableArray<CodeActionOperation> operations = await codeAction
            .GetOperationsAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        ApplyChangesOperation applyChanges = Assert.IsType<ApplyChangesOperation>(
            Assert.Single(operations)
        );
        Document? changedDocument = applyChanges.ChangedSolution.GetDocument(document.Id);
        Assert.NotNull(changedDocument);
        SourceText text = await changedDocument!
            .GetTextAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return text.ToString();
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
