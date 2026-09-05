using DotRocks.Analyzers.Driver;
using DotRocks.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotRocks.Analyzers.Tests;

public sealed class DotRocksDriverSecurityAnalyzerTests
{
    [Theory]
    [InlineData(
        "new DotRocks.FlightSql.DotRocksFlightSqlCommand($\"SELECT {id}\", connection)",
        true
    )]
    [InlineData(
        "new DotRocks.FlightSql.DotRocksFlightSqlCommand(connection: connection, commandText: $\"SELECT {id}\")",
        true
    )]
    [InlineData("new DotRocks.Data.DotRocksCommand($\"SELECT {id}\", missing)", false)]
    [InlineData(
        "new DotRocks.FlightSql.DotRocksFlightSqlCommand(connection: missing, commandText: $\"SELECT {id}\")",
        false
    )]
    public async Task UnboundCommandConstructor_ReportsUnsafeSql(
        string expression,
        bool requireValidCode
    )
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                $$"""
                internal static class Sample
                {
                    public static void Run(string id, dynamic connection)
                    {
                        _ = {{expression}};
                    }
                }
                """,
                requireValidCode
            )
            .ConfigureAwait(true);

        Diagnostic diagnostic = Assert.Single(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksDiagnosticDescriptors.UnsafeCommandTextDiagnosticId
        );
        var sourceText = await diagnostic
            .Location.SourceTree!.GetTextAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("$\"SELECT {id}\"", sourceText.ToString(diagnostic.Location.SourceSpan));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
    }

    [Theory]
    [InlineData("new DotRocks.FlightSql.DotRocksFlightSqlCommand(\"SELECT @id\", connection)")]
    [InlineData(
        "new DotRocks.FlightSql.DotRocksFlightSqlCommand(connection: $\"{id}\", commandText: \"SELECT @id\")"
    )]
    [InlineData("new DotRocks.Data.DotRocksCommand(\"SELECT @id\", missing)")]
    [InlineData("new UnrelatedCommand($\"SELECT {id}\", connection)")]
    public async Task UnboundConstructor_WithoutUnsafeCommandText_DoesNotReport(string expression)
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                $$"""
                internal sealed class UnrelatedCommand
                {
                    public UnrelatedCommand(string commandText, object connection) { }
                }
                internal static class Sample
                {
                    public static void Run(string id, dynamic connection)
                    {
                        _ = {{expression}};
                    }
                }
                """
            )
            .ConfigureAwait(true);

        AssertNoDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.UnsafeCommandTextDiagnosticId
        );
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
    }

    [Theory]
    [InlineData("command?.CommandText = $\"SELECT {id}\";")]
    [InlineData("_ = new DotRocks.Data.DotRocksCommand { CommandText = $\"SELECT {id}\" };")]
    [InlineData("DotRocks.Data.DotRocksCommand other = new($\"SELECT {id}\");")]
    [InlineData(
        "_ = new DotRocks.FlightSql.DotRocksFlightSqlCommand(connection: new(), commandText: $\"SELECT {id}\");"
    )]
    public async Task ModernCommandSyntax_ReportsUnsafeSql(string statement)
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                $$"""
                internal static class Sample
                {
                    public static void Run(DotRocks.Data.DotRocksCommand command, string id)
                    {
                        {{statement}}
                    }
                }
                """,
                requireValidCode: true
            )
            .ConfigureAwait(true);

        Assert.Single(
            diagnostics,
            diagnostic =>
                diagnostic.Id == DotRocksDiagnosticDescriptors.UnsafeCommandTextDiagnosticId
        );
    }

    [Theory]
    [InlineData("command?.CommandText = \"SELECT @id\";")]
    [InlineData("_ = new DotRocks.Data.DotRocksCommand { CommandText = \"SELECT @id\" };")]
    [InlineData("DotRocks.Data.DotRocksCommand other = new(\"SELECT @id\");")]
    public async Task ModernCommandSyntax_WithConstantSql_DoesNotReport(string statement)
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                $$"""
                internal static class Sample
                {
                    public static void Run(DotRocks.Data.DotRocksCommand command)
                    {
                        {{statement}}
                    }
                }
                """,
                requireValidCode: true
            )
            .ConfigureAwait(true);

        AssertNoDiagnostic(
            diagnostics,
            DotRocksDiagnosticDescriptors.UnsafeCommandTextDiagnosticId
        );
    }

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
    public async Task InterpolatedFlightSqlCommandText_ReportsUnsafeSql()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(
                        DotRocks.FlightSql.DotRocksFlightSqlCommand command,
                        string id)
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
    public async Task InterpolatedFlightSqlCommandConstructor_ReportsUnsafeSql()
    {
        Diagnostic[] diagnostics = await AnalyzeAsync(
                """
                internal static class Sample
                {
                    public static void Run(
                        DotRocks.FlightSql.DotRocksFlightSqlDbConnection connection,
                        string id)
                    {
                        _ = new DotRocks.FlightSql.DotRocksFlightSqlCommand(
                            $"SELECT * FROM events WHERE id = {id}",
                            connection);
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
        AnalyzerTestHarness.AssertHasDiagnostic(diagnostics, id);

    private static void AssertNoDiagnostic(Diagnostic[] diagnostics, string id) =>
        AnalyzerTestHarness.AssertNoDiagnostic(diagnostics, id);

    private static Task<Diagnostic[]> AnalyzeAsync(string source, bool requireValidCode = false) =>
        AnalyzerTestHarness.AnalyzeAsync(
            AnalyzerTestHarness.DotRocksStubs + source,
            requireValidCode,
            new UnsafeCommandTextAnalyzer(),
            new MissingCancellationTokenAnalyzer(),
            new SyncOverAsyncAnalyzer(),
            new LiteralPasswordAnalyzer()
        );
}
