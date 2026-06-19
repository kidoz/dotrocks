using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DotRocks.EntityFrameworkCore.Migrations;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The EF Core service provider constructs this internal service through dependency injection."
)]
internal sealed class DotRocksHistoryRepository(HistoryRepositoryDependencies dependencies)
    : HistoryRepository(dependencies),
        IHistoryRepository
{
    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Explicit;

    protected override string ExistsSql =>
        "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = "
        + SchemaLiteral()
        + " AND table_name = "
        + FormatSqlString(TableName);

    protected override string GetAppliedMigrationsSql =>
        "SELECT "
        + SqlGenerationHelper.DelimitIdentifier(MigrationIdColumnName)
        + ", "
        + SqlGenerationHelper.DelimitIdentifier(ProductVersionColumnName)
        + " FROM "
        + SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)
        + " ORDER BY "
        + SqlGenerationHelper.DelimitIdentifier(MigrationIdColumnName);

    public override IMigrationsDatabaseLock AcquireDatabaseLock() =>
        new DotRocksMigrationDatabaseLock(this);

    public override Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IMigrationsDatabaseLock>(new DotRocksMigrationDatabaseLock(this));
    }

    bool IHistoryRepository.CreateIfNotExists()
    {
        ExecuteNonQueryOutsideMigrationTransaction(GetCreateIfNotExistsScript());
        return true;
    }

    async Task<bool> IHistoryRepository.CreateIfNotExistsAsync(CancellationToken cancellationToken)
    {
        await ExecuteNonQueryOutsideMigrationTransactionAsync(
                GetCreateIfNotExistsScript(),
                cancellationToken
            )
            .ConfigureAwait(false);
        return true;
    }

    public override string GetCreateIfNotExistsScript() =>
        "CREATE TABLE IF NOT EXISTS "
        + SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)
        + " ("
        + SqlGenerationHelper.DelimitIdentifier(MigrationIdColumnName)
        + " VARCHAR(150) NOT NULL, "
        + SqlGenerationHelper.DelimitIdentifier(ProductVersionColumnName)
        + " VARCHAR(32) NOT NULL) DUPLICATE KEY("
        + SqlGenerationHelper.DelimitIdentifier(MigrationIdColumnName)
        + ") DISTRIBUTED BY HASH("
        + SqlGenerationHelper.DelimitIdentifier(MigrationIdColumnName)
        + ") BUCKETS 1 PROPERTIES ('replication_num' = '1');"
        + Environment.NewLine;

    public override string GetInsertScript(HistoryRow row) =>
        "INSERT INTO "
        + SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)
        + " ("
        + SqlGenerationHelper.DelimitIdentifier(MigrationIdColumnName)
        + ", "
        + SqlGenerationHelper.DelimitIdentifier(ProductVersionColumnName)
        + ") VALUES ("
        + FormatSqlString(row.MigrationId)
        + ", "
        + FormatSqlString(row.ProductVersion)
        + ");"
        + Environment.NewLine;

    public override string GetDeleteScript(string migrationId) =>
        "DELETE FROM "
        + SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)
        + " WHERE "
        + SqlGenerationHelper.DelimitIdentifier(MigrationIdColumnName)
        + " = "
        + FormatSqlString(migrationId)
        + ";"
        + Environment.NewLine;

    public override string GetBeginIfNotExistsScript(string migrationId) =>
        throw new NotSupportedException(
            "DotRocks EF Core idempotent migration scripts are not supported yet."
        );

    public override string GetBeginIfExistsScript(string migrationId) =>
        throw new NotSupportedException(
            "DotRocks EF Core idempotent migration scripts are not supported yet."
        );

    public override string GetEndIfScript() => string.Empty;

    protected override bool InterpretExistsResult(object? value) =>
        Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;

    protected override void ConfigureTable(EntityTypeBuilder<HistoryRow> history)
    {
        base.ConfigureTable(history);
        history.Property(row => row.MigrationId).HasMaxLength(150).ValueGeneratedNever();
        history.Property(row => row.ProductVersion).HasMaxLength(32).ValueGeneratedNever();
    }

    private string SchemaLiteral() =>
        string.IsNullOrEmpty(TableSchema) ? "DATABASE()" : FormatSqlString(TableSchema);

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command text is generated by the provider from escaped migration history identifiers."
    )]
    private void ExecuteNonQueryOutsideMigrationTransaction(string commandText)
    {
        bool opened = Dependencies.Connection.Open();
        try
        {
            using DbCommand command = Dependencies.Connection.DbConnection.CreateCommand();
            command.CommandText = commandText;
            command.ExecuteNonQuery();
        }
        finally
        {
            if (opened)
            {
                Dependencies.Connection.Close();
            }
        }
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command text is generated by the provider from escaped migration history identifiers."
    )]
    private async Task ExecuteNonQueryOutsideMigrationTransactionAsync(
        string commandText,
        CancellationToken cancellationToken
    )
    {
        bool opened = await Dependencies
            .Connection.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using DbCommand command = Dependencies.Connection.DbConnection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (opened)
            {
                await Dependencies.Connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static string FormatSqlString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');
        foreach (char character in value)
        {
            if (character == '\'')
            {
                builder.Append("''");
            }
            else
            {
                builder.Append(character);
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }
}
