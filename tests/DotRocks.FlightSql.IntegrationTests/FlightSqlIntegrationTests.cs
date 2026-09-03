using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotRocks.Data;
using DotRocks.FlightSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotRocks.FlightSql.IntegrationTests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "Await-using declarations in xUnit tests intentionally retain the test context."
)]
public sealed class FlightSqlIntegrationTests
{
    [Fact]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The database identifier is generated internally and values are parameterized."
    )]
    public async Task FlightReadsAndExplicitMySqlWrites_RunAgainstStarRocks()
    {
        FlightSqlIntegrationEnvironment.SkipUnlessEnabled();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string database =
            "dotrocks_flight_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        await using var mysql = new DotRocksConnection(
            FlightSqlIntegrationEnvironment.MySqlConnectionString
        );
        await mysql.OpenAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            await ExecuteMySqlAsync(mysql, $"CREATE DATABASE `{database}`", cancellationToken)
                .ConfigureAwait(true);
            await ExecuteMySqlAsync(
                    mysql,
                    $"CREATE TABLE `{database}`.`widgets` (id INT NOT NULL, name VARCHAR(64) NOT NULL) "
                        + "PRIMARY KEY(id) DISTRIBUTED BY HASH(id) BUCKETS 1 "
                        + "PROPERTIES('replication_num'='1')",
                    cancellationToken
                )
                .ConfigureAwait(true);

            await using var flight = new DotRocksFlightSqlDbConnection(
                FlightSqlIntegrationEnvironment.FlightOptions,
                FlightSqlIntegrationEnvironment.MySqlConnectionString,
                DotRocksFlightSqlFallbackMode.WriteCommands
            );
            await flight.OpenAsync(cancellationToken).ConfigureAwait(true);
            await using DotRocksFlightSqlCommand insert = flight.CreateCommand();
            insert.CommandText = $"INSERT INTO `{database}`.`widgets` VALUES (@id, @name)";
            var idParameter = new DotRocksParameter { ParameterName = "id", Value = 1 };
            var nameParameter = new DotRocksParameter { ParameterName = "name", Value = "flight" };
            insert.Parameters.Add(idParameter);
            insert.Parameters.Add(nameParameter);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
            idParameter.Value = 3;
            nameParameter.Value = "fallback-reuse";
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);

            await using DotRocksFlightSqlCommand query = flight.CreateCommand();
            query.CommandText = $"SELECT id, name FROM `{database}`.`widgets` ORDER BY id";
            await using DbDataReader reader = await query
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(true);
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("flight", reader.GetString(1));
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(3, reader.GetInt32(0));
            Assert.Equal("fallback-reuse", reader.GetString(1));
            await reader.DisposeAsync().ConfigureAwait(true);

            var optionsBuilder = new DbContextOptionsBuilder<FlightContext>();
            optionsBuilder.UseStarRocks(flight);
            await using var context = new FlightContext(optionsBuilder.Options, database);
            Widget widget = await context
                .Widgets.SingleAsync(item => item.Id == 1, cancellationToken)
                .ConfigureAwait(true);
            Assert.Equal("flight", widget.Name);

            context.Widgets.Add(new Widget { Id = 2, Name = "ef-flight" });
            Assert.Equal(1, await context.SaveChangesAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(
                3,
                await context.Widgets.CountAsync(cancellationToken).ConfigureAwait(true)
            );
        }
        finally
        {
            await ExecuteMySqlAsync(
                    mysql,
                    $"DROP DATABASE IF EXISTS `{database}`",
                    CancellationToken.None
                )
                .ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task FlightQueries_DoNotLeakStarRocksSessions()
    {
        FlightSqlIntegrationEnvironment.SkipUnlessEnabled();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var mysql = new DotRocksConnection(
            FlightSqlIntegrationEnvironment.MySqlConnectionString
        );
        await mysql.OpenAsync(cancellationToken).ConfigureAwait(true);
        int before = await CountSessionsAsync(mysql, cancellationToken).ConfigureAwait(true);

        var dataSource = new DotRocksFlightSqlDataSource(
            FlightSqlIntegrationEnvironment.FlightOptions
        );
        await using (dataSource.ConfigureAwait(true))
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                await using DotRocksFlightSqlDbConnection flight = dataSource.CreateConnection();
                await flight.OpenAsync(cancellationToken).ConfigureAwait(true);
                await using DotRocksFlightSqlCommand command = flight.CreateCommand();
                command.CommandText = "SELECT 1";
                await using DbDataReader reader = await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(true);
                Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            }
        }

        int after = await CountSessionsAsync(mysql, cancellationToken).ConfigureAwait(true);

        // Before session authentication, each of these 20 RPCs left its own sleeping frontend
        // connection behind, which exhausted the per-user connection limit during benchmarks.
        // StarRocks 4.0 releases the session on disposal through the Flight SQL CloseSession
        // action and settles at zero; 3.5 does not implement that action, so its single
        // handshake session lingers until the server expires it. Either way the cost is per data
        // source, not per RPC.
        Assert.True(
            after - before <= 1,
            $"Twenty Flight RPCs left {after - before} extra StarRocks sessions behind."
        );
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The database identifier is generated internally and the values are fixed."
    )]
    public async Task FlightDateTimeValues_MatchTheMySqlProtocolUnderANonUtcSessionTimeZone()
    {
        FlightSqlIntegrationEnvironment.SkipUnlessEnabled();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string database =
            "dotrocks_flight_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        await using var mysql = new DotRocksConnection(
            FlightSqlIntegrationEnvironment.MySqlConnectionString
        );
        await mysql.OpenAsync(cancellationToken).ConfigureAwait(true);
        string originalTimeZone = await ScalarStringAsync(
                mysql,
                "SELECT @@GLOBAL.time_zone",
                cancellationToken
            )
            .ConfigureAwait(true);

        try
        {
            await ExecuteMySqlAsync(mysql, $"CREATE DATABASE `{database}`", cancellationToken)
                .ConfigureAwait(true);
            await ExecuteMySqlAsync(
                    mysql,
                    $"CREATE TABLE `{database}`.`events` (id INT NOT NULL, occurred_at DATETIME NOT NULL) "
                        + "PRIMARY KEY(id) DISTRIBUTED BY HASH(id) BUCKETS 1 "
                        + "PROPERTIES('replication_num'='1')",
                    cancellationToken
                )
                .ConfigureAwait(true);
            await ExecuteMySqlAsync(
                    mysql,
                    $"INSERT INTO `{database}`.`events` VALUES (1, '2026-06-19 12:34:56.123456')",
                    cancellationToken
                )
                .ConfigureAwait(true);

            // A DATETIME carries no zone: whatever the session zone, both transports must hand
            // back the stored wall-clock value. The zone is changed globally so the fresh Flight
            // session below inherits it, and restored afterwards.
            await ExecuteMySqlAsync(
                    mysql,
                    "SET GLOBAL time_zone = 'Asia/Shanghai'",
                    cancellationToken
                )
                .ConfigureAwait(true);
            var expected = new DateTime(2026, 6, 19, 12, 34, 56, 123).AddTicks(4560);
            string query = $"SELECT occurred_at FROM `{database}`.`events` WHERE id = 1";

            await using var mysqlSession = new DotRocksConnection(
                FlightSqlIntegrationEnvironment.MySqlConnectionString
            );
            await mysqlSession.OpenAsync(cancellationToken).ConfigureAwait(true);
            await using DbCommand mysqlQuery = mysqlSession.CreateCommand();
            mysqlQuery.CommandText = query;
            await using DbDataReader mysqlReader = await mysqlQuery
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(true);
            Assert.True(await mysqlReader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(expected, mysqlReader.GetDateTime(0));

            await using var flight = new DotRocksFlightSqlDbConnection(
                FlightSqlIntegrationEnvironment.FlightOptions
            );
            await flight.OpenAsync(cancellationToken).ConfigureAwait(true);
            await using DotRocksFlightSqlCommand flightQuery = flight.CreateCommand();
            flightQuery.CommandText = query;
            await using DbDataReader flightReader = await flightQuery
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(true);
            Assert.True(await flightReader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(typeof(DateTime), flightReader.GetFieldType(0));
            Assert.Equal(expected, flightReader.GetDateTime(0));
        }
        finally
        {
            await ExecuteMySqlAsync(
                    mysql,
                    $"SET GLOBAL time_zone = '{originalTimeZone}'",
                    CancellationToken.None
                )
                .ConfigureAwait(true);
            await ExecuteMySqlAsync(
                    mysql,
                    $"DROP DATABASE IF EXISTS `{database}`",
                    CancellationToken.None
                )
                .ConfigureAwait(true);
        }
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The caller supplies only fixed SQL."
    )]
    private static async Task<string> ScalarStringAsync(
        DotRocksConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Assert.IsType<string>(value);
    }

    private static async Task<int> CountSessionsAsync(
        DotRocksConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SHOW PROCESSLIST";
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        int count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The caller supplies only fixed SQL with internally generated identifiers."
    )]
    private static async Task ExecuteMySqlAsync(
        DotRocksConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class FlightContext(DbContextOptions<FlightContext> options, string database)
        : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().ToTable("widgets", database).HasKey(widget => widget.Id);
            modelBuilder.Entity<Widget>().Property(widget => widget.Id).ValueGeneratedNever();
        }
    }

    private sealed class Widget
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
