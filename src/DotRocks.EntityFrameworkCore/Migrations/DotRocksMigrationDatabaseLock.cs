using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DotRocks.EntityFrameworkCore.Migrations;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "DotRocksHistoryRepository creates this lock implementation."
)]
internal sealed class DotRocksMigrationDatabaseLock(IHistoryRepository historyRepository)
    : IMigrationsDatabaseLock
{
    // This token is created only after the caller explicitly declares an external lock. Its
    // lifetime belongs to that external coordinator; disposing EF's token must not release it.
    public IHistoryRepository HistoryRepository { get; } = historyRepository;

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
