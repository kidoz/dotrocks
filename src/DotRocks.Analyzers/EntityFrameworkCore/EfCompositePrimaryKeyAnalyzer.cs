using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotRocks.Analyzers.EntityFrameworkCore;

/// <summary>
/// Retired analyzer that formerly reported DTR0008 for EF Core entities configured with a
/// composite primary key. DotRocks EF Core supports composite primary keys, so this analyzer no
/// longer reports any diagnostic. The public type is preserved as an obsolete no-op because it
/// shipped in DotRocks.Analyzers 1.0.1+ and the project follows semantic versioning; it will be
/// removed in the next major release.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
[Obsolete(
    "Composite primary keys are supported by DotRocks EF Core; DTR0008 no longer reports and this analyzer will be removed in the next major release."
)]
public sealed class EfCompositePrimaryKeyAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
    }
}
