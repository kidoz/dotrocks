using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Query;

namespace DotRocks.EntityFrameworkCore.Query;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksMethodCallTranslatorProvider : RelationalMethodCallTranslatorProvider
{
    public DotRocksMethodCallTranslatorProvider(
        RelationalMethodCallTranslatorProviderDependencies dependencies
    )
        : base(dependencies)
    {
        AddTranslators([new DotRocksStringMethodTranslator(dependencies.SqlExpressionFactory)]);
    }
}
