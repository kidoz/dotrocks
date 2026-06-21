using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotRocks.Data;
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

    // EF Core wraps the whole migration in an explicit transaction, but StarRocks rejects every
    // non-INSERT statement inside one (3.5 allows INSERT only). The history-table reads below would
    // otherwise run inside that transaction, so they execute on a separate, transaction-free
    // connection. With the read queries moved out, and the generated DDL plus the history
    // INSERT/DELETE already transaction-suppressed, the migration transaction stays empty and
    // commits cleanly across StarRocks versions.

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command text is the provider-generated history existence query over escaped identifiers."
    )]
    public override bool Exists()
    {
        using DotRocksConnection connection = CreateNonTransactionalConnection();
        if (!TryOpen(connection))
        {
            return false;
        }

        using DbCommand command = connection.CreateCommand();
        command.CommandText = ExistsSql;
        return InterpretExistsResult(command.ExecuteScalar());
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command text is the provider-generated history existence query over escaped identifiers."
    )]
    public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        DotRocksConnection connection = CreateNonTransactionalConnection();
        await using var connectionScope = connection.ConfigureAwait(false);
        if (!await TryOpenAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        using DbCommand command = connection.CreateCommand();
        command.CommandText = ExistsSql;
        return InterpretExistsResult(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
        );
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command text is the provider-generated applied-migrations query over escaped identifiers."
    )]
    public override IReadOnlyList<HistoryRow> GetAppliedMigrations()
    {
        if (!Exists())
        {
            return [];
        }

        var rows = new List<HistoryRow>();
        using DotRocksConnection connection = CreateNonTransactionalConnection();
        connection.Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = GetAppliedMigrationsSql;
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new HistoryRow(reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command text is the provider-generated applied-migrations query over escaped identifiers."
    )]
    public override async Task<IReadOnlyList<HistoryRow>> GetAppliedMigrationsAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (!await ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var rows = new List<HistoryRow>();
        DotRocksConnection connection = CreateNonTransactionalConnection();
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = GetAppliedMigrationsSql;
        DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new HistoryRow(reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    private DotRocksConnection CreateNonTransactionalConnection()
    {
        string? connectionString = Dependencies.Connection.ConnectionString;
        return connectionString is null
            ? throw new InvalidOperationException(
                "The migration connection has no connection string to open a transaction-free history connection."
            )
            : new DotRocksConnection(connectionString);
    }

    private static bool TryOpen(DotRocksConnection connection)
    {
        try
        {
            connection.Open();
            return true;
        }
        catch (DotRocksException)
        {
            // The database does not exist (StarRocks refuses login to a missing database), so the
            // history table cannot exist either.
            return false;
        }
    }

    private static async Task<bool> TryOpenAsync(
        DotRocksConnection connection,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DotRocksException)
        {
            return false;
        }
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
