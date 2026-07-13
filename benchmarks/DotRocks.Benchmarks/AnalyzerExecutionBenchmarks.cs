using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using DotRocks.Analyzers.Driver;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Benchmarks;

/// <summary>Benchmarks analyzer execution on a generated representative compilation.</summary>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Local)]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires public benchmark types."
)]
public class AnalyzerExecutionBenchmarks
{
    private CSharpCompilation _compilation = null!;
    private ImmutableArray<DiagnosticAnalyzer> _analyzers;

    [GlobalSetup]
    public void Setup()
    {
        const int invocationCount = 500;
        var source = new System.Text.StringBuilder(
            "using System.Threading; using System.Threading.Tasks; "
                + "namespace DotRocks.Data { public sealed class Connection { "
                + "public Task ExecuteAsync(CancellationToken token = default) => Task.CompletedTask; } } "
                + "public sealed class Consumer { public async Task Run(CancellationToken token) { "
        );
        for (int i = 0; i < invocationCount; i++)
        {
            source.Append("await new DotRocks.Data.Connection().ExecuteAsync();");
        }

        source.Append("} }");
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source.ToString());
        MetadataReference[] references =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
        ];
        _compilation = CSharpCompilation.Create(
            "RepresentativeConsumer",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        _analyzers = [new MissingCancellationTokenAnalyzer(), new SyncOverAsyncAnalyzer()];
    }

    [Benchmark]
    public Task<ImmutableArray<Diagnostic>> AnalyzeRepresentativeCompilation() =>
        _compilation.WithAnalyzers(_analyzers).GetAnalyzerDiagnosticsAsync();
}
