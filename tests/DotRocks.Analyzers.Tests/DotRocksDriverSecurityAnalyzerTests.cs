using System.Collections.Immutable;
using DotRocks.Analyzers.Driver;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DotRocks.Analyzers.Tests;

public sealed class DotRocksDriverSecurityAnalyzerTests
{
    [Fact]
    public async Task ConcatenatedCommandText_ReportsUnsafeSql()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(DotRocks.Data.DotRocksCommand command, string id)
                    {
                        command.CommandText = "SELECT * FROM events WHERE id = " + id;
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertHasDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.UnsafeCommandTextDiagnosticId
        );
    }

    [Fact]
    public async Task InterpolatedCommandText_ReportsUnsafeSql()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(DotRocks.Data.DotRocksCommand command, string id)
                    {
                        command.CommandText = $"SELECT * FROM events WHERE id = {id}";
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertHasDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.UnsafeCommandTextDiagnosticId
        );
    }

    [Fact]
    public async Task InterpolatedCommandConstructor_ReportsUnsafeSql()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(string id)
                    {
                        _ = new DotRocks.Data.DotRocksCommand($"SELECT * FROM events WHERE id = {id}");
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertHasDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.UnsafeCommandTextDiagnosticId
        );
    }

    [Fact]
    public async Task ConstantCommandText_DoesNotReportUnsafeSql()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(DotRocks.Data.DotRocksCommand command, string id)
                    {
                        command.CommandText = "SELECT * FROM events WHERE id = @id";
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertNoDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.UnsafeCommandTextDiagnosticId
        );
    }

    [Fact]
    public async Task ParameterCommandText_DoesNotReportUnsafeSql()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(DotRocks.Data.DotRocksCommand command, string sql)
                    {
                        command.CommandText = sql;
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertNoDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.UnsafeCommandTextDiagnosticId
        );
    }

    [Fact]
    public async Task MissingCancellationToken_ReportsWhenTokenAvailable()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static async System.Threading.Tasks.Task Run(
                        DotRocks.Data.DotRocksCommand command,
                        System.Threading.CancellationToken cancellationToken)
                    {
                        await command.ExecuteNonQueryAsync();
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertHasDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.MissingCancellationTokenDiagnosticId
        );
    }

    [Fact]
    public async Task PassedCancellationToken_DoesNotReport()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static async System.Threading.Tasks.Task Run(
                        DotRocks.Data.DotRocksCommand command,
                        System.Threading.CancellationToken cancellationToken)
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertNoDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.MissingCancellationTokenDiagnosticId
        );
    }

    [Fact]
    public async Task MissingCancellationToken_DoesNotReportWhenNoTokenInScope()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static async System.Threading.Tasks.Task Run(
                        DotRocks.Data.DotRocksCommand command)
                    {
                        await command.ExecuteNonQueryAsync();
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertNoDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.MissingCancellationTokenDiagnosticId
        );
    }

    [Fact]
    public async Task MissingCancellationToken_ReportsInsideLocalFunctionWhenTokenAvailable()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(
                        DotRocks.Data.DotRocksCommand command,
                        System.Threading.CancellationToken cancellationToken)
                    {
                        async System.Threading.Tasks.Task QueryAsync()
                        {
                            await command.ExecuteNonQueryAsync();
                        }
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertHasDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.MissingCancellationTokenDiagnosticId
        );
    }

    [Fact]
    public async Task BlockingResult_ReportsSyncOverAsync()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(DotRocks.Data.DotRocksCommand command)
                    {
                        int rows = command.ExecuteNonQueryAsync().Result;
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertHasDiagnostic(diagnostics, DotRocksDiagnosticDescriptors.SyncOverAsyncDiagnosticId);
    }

    [Fact]
    public async Task BlockingGetAwaiterGetResult_ReportsSyncOverAsync()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(DotRocks.Data.DotRocksCommand command)
                    {
                        command.ExecuteNonQueryAsync().GetAwaiter().GetResult();
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertHasDiagnostic(diagnostics, DotRocksDiagnosticDescriptors.SyncOverAsyncDiagnosticId);
    }

    [Fact]
    public async Task AwaitedCall_DoesNotReportSyncOverAsync()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static async System.Threading.Tasks.Task Run(
                        DotRocks.Data.DotRocksCommand command)
                    {
                        await command.ExecuteNonQueryAsync();
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertNoDiagnostic(diagnostics, DotRocksDiagnosticDescriptors.SyncOverAsyncDiagnosticId);
    }

    [Fact]
    public async Task NonDotRocksBlockingResult_DoesNotReportSyncOverAsync()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run()
                    {
                        int value = System.Threading.Tasks.Task.FromResult(0).Result;
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertNoDiagnostic(diagnostics, DotRocksDiagnosticDescriptors.SyncOverAsyncDiagnosticId);
    }

    [Fact]
    public async Task LiteralPasswordLiteral_ReportsDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run()
                    {
                        _ = new DotRocks.Data.DotRocksConnection("Server=127.0.0.1;User ID=root;Password=secret");
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertHasDiagnostic(diagnostics, DotRocksDiagnosticDescriptors.LiteralPasswordDiagnosticId);
    }

    [Fact]
    public async Task LiteralPasswordAlias_ReportsDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run()
                    {
                        _ = new DotRocks.Data.DotRocksConnection("Server=127.0.0.1;User ID=root;Pwd=secret");
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertHasDiagnostic(diagnostics, DotRocksDiagnosticDescriptors.LiteralPasswordDiagnosticId);
    }

    [Fact]
    public async Task LiteralPasswordLocalVariable_ReportsDiagnostic()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run()
                    {
                        var connectionString = "Server=127.0.0.1;User ID=root;Password=secret";
                        _ = new DotRocks.Data.DotRocksDataSource(connectionString);
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertHasDiagnostic(diagnostics, DotRocksDiagnosticDescriptors.LiteralPasswordDiagnosticId);
    }

    [Fact]
    public async Task EmptyLiteralPassword_DoesNotReport()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run()
                    {
                        _ = new DotRocks.Data.DotRocksConnection("Server=127.0.0.1;User ID=root;Password= ");
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertNoDiagnostic(diagnostics, DotRocksDiagnosticDescriptors.LiteralPasswordDiagnosticId);
    }

    [Fact]
    public async Task ConnectionStringWithoutPassword_DoesNotReport()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run()
                    {
                        _ = new DotRocks.Data.DotRocksConnection("Server=127.0.0.1;User ID=root");
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertNoDiagnostic(diagnostics, DotRocksDiagnosticDescriptors.LiteralPasswordDiagnosticId);
    }

    [Fact]
    public async Task BrokenCode_DoesNotCrashAnalyzers()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(DotRocks.Data.DotRocksCommand command)
                    {
                        command.CommandText = "SELECT " +
                """
            )
            .ConfigureAwait(true);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
    }

    private static void AssertHasDiagnostic(Diagnostic[] diagnostics, string id) =>
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == id);

    private static void AssertNoDiagnostic(Diagnostic[] diagnostics, string id) =>
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == id);

    private static async Task<Diagnostic[]> AnalyzeAsync(string source)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "DotRocks.Analyzers.Tests.DriverSecurityTarget",
            [
                CSharpSyntaxTree.ParseText(
                    DotRocksStubs + source,
                    CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)
                ),
            ],
            CreateReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        DiagnosticAnalyzer[] analyzers =
        [
            new UnsafeCommandTextAnalyzer(),
            new MissingCancellationTokenAnalyzer(),
            new SyncOverAsyncAnalyzer(),
            new LiteralPasswordAnalyzer(),
        ];
        CompilationWithAnalyzers compilationWithAnalyzers = compilation.WithAnalyzers([
            .. analyzers,
        ]);
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

            public sealed class DotRocksCommand
            {
                public DotRocksCommand() { }

                public DotRocksCommand(string commandText) { }

                public string CommandText { get; set; } = string.Empty;

                public System.Threading.Tasks.Task<int> ExecuteNonQueryAsync(
                    System.Threading.CancellationToken cancellationToken = default) =>
                    System.Threading.Tasks.Task.FromResult(0);
            }
        }

        """;
}
