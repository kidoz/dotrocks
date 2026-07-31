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
