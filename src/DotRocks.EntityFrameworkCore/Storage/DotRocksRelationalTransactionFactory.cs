using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace DotRocks.EntityFrameworkCore.Storage;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksRelationalTransactionFactory(
    RelationalTransactionFactoryDependencies dependencies
) : IRelationalTransactionFactory
{
    public RelationalTransaction Create(
        IRelationalConnection connection,
        DbTransaction transaction,
        Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
        bool transactionOwned
    ) =>
        new DotRocksRelationalTransaction(
            connection,
            transaction,
            transactionId,
            logger,
            transactionOwned,
            dependencies.SqlGenerationHelper
        );
}

internal sealed class DotRocksRelationalTransaction(
    IRelationalConnection connection,
    DbTransaction transaction,
    Guid transactionId,
    IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
    bool transactionOwned,
    ISqlGenerationHelper sqlGenerationHelper
)
    : RelationalTransaction(
        connection,
        transaction,
        transactionId,
        logger,
        transactionOwned,
        sqlGenerationHelper
    )
{
    // StarRocks does not support SAVEPOINT. Declaring savepoints unsupported makes EF Core skip
    // them (e.g. when SaveChanges runs inside a user-managed transaction) instead of emitting a
    // SAVEPOINT statement that StarRocks rejects with a syntax error.
    public override bool SupportsSavepoints => false;
}
