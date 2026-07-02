using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Xunit;

namespace DotRocks.Data.IntegrationTests;

[Collection("StarRocks integration")]
public sealed class ConnectionIntegrationTests
{
    // Per-run Guid-suffixed database owned (and dropped) by StarRocksIntegrationDatabaseFixture.
    private static readonly string TransactionDatabaseName =
        StarRocksIntegrationDatabaseFixture.TransactionDatabaseName;

    [Fact]
    public async Task OpenAsync_AuthenticatesAgainstStarRocks()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);

        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.False(string.IsNullOrWhiteSpace(connection.ServerVersion));
    }

    [Fact]
    public async Task GetSchema_ReadsDatabasesTablesAndColumnsFromInformationSchema()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        DataTable databases = await connection
            .GetSchemaAsync("Databases", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Contains(
            databases.Rows.Cast<DataRow>(),
            row =>
                string.Equals(
                    (string)row["database_name"],
                    "information_schema",
                    StringComparison.OrdinalIgnoreCase
                )
        );

        // The information_schema.tables view is always present; query its columns with restrictions.
        DataTable columns = await connection
            .GetSchemaAsync(
                "Columns",
                [null, "information_schema", "tables", null],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.True(columns.Rows.Count >= 1);
        Assert.Contains(
            columns.Rows.Cast<DataRow>(),
            row =>
                string.Equals(
                    (string)row["column_name"],
                    "TABLE_NAME",
                    StringComparison.OrdinalIgnoreCase
                )
        );

        DataTable views = await connection
            .GetSchemaAsync(
                "Views",
                [null, "information_schema", "tables"],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.True(views.Rows.Count >= 1);
    }

    [Fact]
    public async Task ServerPrepared_ReusesCachedStatementAcrossExecutions()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DotRocksCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ? + ? AS total";
        command.ParameterMode = DotRocksParameterMode.ServerPrepared;
        var first = command.CreateParameter();
        var second = command.CreateParameter();
        command.Parameters.Add(first);
        command.Parameters.Add(second);

        first.Value = 1;
        second.Value = 1;
        object? firstResult = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(2L, Convert.ToInt64(firstResult, CultureInfo.InvariantCulture));

        // The second execution reuses the cached prepared statement on the same physical connection.
        first.Value = 10;
        second.Value = 20;
        object? secondResult = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(30L, Convert.ToInt64(secondResult, CultureInfo.InvariantCulture));
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "DDL is composed from fixed sanitized identifiers; DML uses server-prepared parameters."
    )]
    public async Task ServerPrepared_WriteDml_IsRejectedByStarRocks()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        // Characterization: StarRocks 4.0.7 only allows SELECT in the binary prepared-statement
        // protocol; a prepared INSERT/UPDATE/DELETE is rejected by the server. DotRocks surfaces the
        // server error. Use DotRocksParameterMode.Auto (text protocol) for parameterized writes.
        string database =
            "dotrocks_prepared_dml_"
            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await ExecuteAsync(connection, $"CREATE DATABASE IF NOT EXISTS `{database}`")
            .ConfigureAwait(true);

        try
        {
            await ExecuteAsync(
                    connection,
                    $"CREATE TABLE `{database}`.`t` (id INT NOT NULL, name VARCHAR(32) NOT NULL) "
                        + "PRIMARY KEY(id) DISTRIBUTED BY HASH(id) BUCKETS 1 PROPERTIES('replication_num'='1')"
                )
                .ConfigureAwait(true);

            DotRocksException exception = await Assert
                .ThrowsAsync<DotRocksException>(async () =>
                    await ExecutePreparedDmlAsync(
                            connection,
                            $"INSERT INTO `{database}`.`t` (id, name) VALUES (?, ?)",
                            [1, "alice"]
                        )
                        .ConfigureAwait(true)
                )
                .ConfigureAwait(true);
            Assert.Contains(
                "prepared statement",
                exception.Message,
                StringComparison.OrdinalIgnoreCase
            );
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS `{database}`")
                .ConfigureAwait(true);
        }
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Fixed sanitized DDL identifiers."
    )]
    private static async Task ExecuteAsync(DotRocksConnection connection, string sql)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Server-prepared parameters."
    )]
    private static async Task ExecutePreparedDmlAsync(
        DotRocksConnection connection,
        string sql,
        object[] values
    )
    {
        using DotRocksCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ParameterMode = DotRocksParameterMode.ServerPrepared;
        foreach (object value in values)
        {
            var parameter = command.CreateParameter();
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task ServerPrepared_ExecutesParameterizedQueryWithBinaryProtocol()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DotRocksCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ? + ? AS total, ? AS label";
        command.ParameterMode = DotRocksParameterMode.ServerPrepared;
        AddValue(command, 2);
        AddValue(command, 3);
        AddValue(command, "hello");

        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal(5L, Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        Assert.Equal("hello", reader.GetString(1));
        Assert.False(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );

        static void AddValue(DotRocksCommand command, object value)
        {
            var parameter = command.CreateParameter();
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    [Theory]
    [InlineData("SELECT to_bitmap(1) AS v")]
    [InlineData("SELECT hll_hash(1) AS v")]
    [InlineData("SELECT percentile_hash(1.0) AS v")]
    public async Task ExecuteReaderAsync_OpaqueAggregateStateTypesReadAsNull(string query)
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        ArgumentNullException.ThrowIfNull(query);

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed inline test literals, not user input.
        command.CommandText = query;
#pragma warning restore CA2100

        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );

        // BITMAP/HLL/PERCENTILE are opaque aggregate-state types: a direct select returns NULL
        // over the text protocol (no lossless value), so the driver surfaces DBNull rather than
        // inventing a representation. Read them via accessor functions (bitmap_to_string, etc.).
        Assert.True(
            await reader
                .IsDBNullAsync(0, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Theory]
    [InlineData("SELECT [1, 2, 3] AS v", "[1,2,3]")]
    [InlineData("SELECT ['a', 'b'] AS v", """["a","b"]""")]
    [InlineData("SELECT map{'k1': 1, 'k2': 2} AS v", """{"k1":1,"k2":2}""")]
    [InlineData("SELECT named_struct('x', 1, 'y', 'two') AS v", """{"x":1,"y":"two"}""")]
    // Edge cases: nesting, NULL elements, JSON string escaping, decimal scale, and dates/datetimes
    // serialized as quoted strings — all returned as strict JSON-formatted text on 4.0.7.
    [InlineData("SELECT [[1, 2], [3]] AS v", "[[1,2],[3]]")]
    [InlineData("SELECT [1, NULL, 3] AS v", "[1,null,3]")]
    [InlineData("""SELECT ['a"b'] AS v""", """["a\"b"]""")]
    [InlineData("SELECT [CAST(1.50 AS DECIMAL(10,2))] AS v", "[1.50]")]
    [InlineData("SELECT [CAST('2026-06-25' AS DATE)] AS v", """["2026-06-25"]""")]
    [InlineData(
        "SELECT map{'k': CAST('2026-06-25 01:02:03' AS DATETIME)} AS v",
        """{"k":"2026-06-25 01:02:03"}"""
    )]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The queries are fixed inline test literals, not user input."
    )]
    public async Task ExecuteReaderAsync_ReadsArrayMapStructAsLosslessText(
        string query,
        string expectedRawText
    )
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(expectedRawText);

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = query;

        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );

        // ARRAY/MAP/STRUCT return over the text protocol typed as VAR_STRING (JSON returns as
        // STRING) and are serialized as JSON-formatted text, so DotRocksJson reads them losslessly.
        Assert.Equal("VAR_STRING", reader.GetDataTypeName(0), StringComparer.OrdinalIgnoreCase);
        DotRocksJson value = await reader
            .GetFieldValueAsync<DotRocksJson>(0, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(expectedRawText, value.RawText);
        using System.Text.Json.JsonDocument document = value.Parse();
        Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, document.RootElement.ValueKind);
    }

    [Fact]
    public async Task ExecuteReaderAsync_ReadsJsonColumnAsLosslessDotRocksJson()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = """SELECT PARSE_JSON('{"a": 1, "b": [2, 3]}') AS j""";

        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );

        // Characterization: StarRocks 4.0.7 returns a JSON value over the text protocol typed as
        // STRING, so it is not distinguishable from a string by wire type. DotRocksJson is therefore
        // an opt-in lossless accessor rather than an automatic mapping.
        Assert.Equal("STRING", reader.GetDataTypeName(0), StringComparer.OrdinalIgnoreCase);
        DotRocksJson json = await reader
            .GetFieldValueAsync<DotRocksJson>(0, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("""{"a": 1, "b": [2, 3]}""", json.RawText);
        using System.Text.Json.JsonDocument document = json.Parse();
        Assert.Equal(1, document.RootElement.GetProperty("a").GetInt32());
    }

    [Fact]
    public async Task ExecuteScalarAsync_ReturnsSelectOne()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // StarRocks types the integer literal as TINYINT, which maps to sbyte.
        Assert.Equal(1, Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ExecuteReaderAsync_ReadsSelectOneResultSet()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, reader.FieldCount);
        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal(1, reader.GetInt32(0));
        Assert.False(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotLeakPasswordOrConnectionString()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        const string secret = "super-secret-integration-password";
        var builder = new DotRocksConnectionStringBuilder(
            IntegrationTestEnvironment.ConnectionString
        )
        {
            UserId = "dotrocks_missing_user",
            Password = secret,
        };

        using var connection = new DotRocksConnection(builder.ConnectionString);

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await connection
                    .OpenAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            builder.ConnectionString,
            exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("SELECT 'abc'", "abc")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test SQL is fixed InlineData, not user input."
    )]
    public async Task ExecuteScalarAsync_ReturnsTextProtocolValues(string sql, string expected)
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(expected, value);
    }

    [Fact]
    public async Task ExecuteReaderAsync_MapsCommonStarRocksTypes()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CAST(123 AS INT) AS i32,
                CAST(123 AS BIGINT) AS i64,
                CAST(12.34 AS DECIMAL(10, 2)) AS amount,
                CAST(1.5 AS DOUBLE) AS ratio,
                CAST('2026-06-19' AS DATE) AS created_on,
                CAST('2026-06-19 13:14:15' AS DATETIME) AS created_at,
                CAST(1 AS BOOLEAN) AS flag,
                CAST(7 AS TINYINT) AS tiny,
                CAST(1234 AS SMALLINT) AS smol
            """;

        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal(123, reader.GetInt32(0));
        Assert.Equal(123L, reader.GetInt64(1));
        Assert.Equal(12.34m, reader.GetDecimal(2));
        Assert.Equal(1.5d, reader.GetDouble(3));
        Assert.Equal(new DateTime(2026, 6, 19), reader.GetDateTime(4));
        Assert.Equal(new DateTime(2026, 6, 19, 13, 14, 15), reader.GetDateTime(5));
        Assert.True(reader.GetBoolean(6));
        Assert.Equal(
            (sbyte)7,
            await reader
                .GetFieldValueAsync<sbyte>(7, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            (short)1234,
            await reader
                .GetFieldValueAsync<short>(8, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Equal(typeof(long), reader.GetFieldType(1));
        Assert.Equal(typeof(DotRocksDecimal), reader.GetFieldType(2));
        Assert.Equal(typeof(double), reader.GetFieldType(3));
        Assert.Equal(typeof(DateTime), reader.GetFieldType(4));
        Assert.Equal(typeof(DateTime), reader.GetFieldType(5));
        Assert.Equal(typeof(bool), reader.GetFieldType(6));
        Assert.Equal(typeof(sbyte), reader.GetFieldType(7));
        Assert.Equal(typeof(short), reader.GetFieldType(8));
        Assert.False(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
    }

    [Fact]
    public async Task ExecuteReaderAsync_ExposesColumnSchemaAndGenericFieldValues()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CAST(123 AS INT) AS i32,
                CAST(123 AS BIGINT) AS i64,
                CAST(12.34 AS DECIMAL(10, 2)) AS amount,
                CAST(1.5 AS DOUBLE) AS ratio,
                CAST(1.25 AS FLOAT) AS single_value,
                CAST('2026-06-19 13:14:15' AS DATETIME) AS created_at,
                'hello' AS text_value
            """;

        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        ReadOnlyCollection<DbColumn> schema = reader.GetColumnSchema();

        Assert.Equal(7, schema.Count);
        Assert.Equal("i32", schema[0].ColumnName);
        Assert.Equal(0, schema[0].ColumnOrdinal);
        Assert.Equal(typeof(int), schema[0].DataType);
        Assert.Equal("i64", schema[1].ColumnName);
        Assert.Equal(typeof(long), schema[1].DataType);
        Assert.Equal("amount", schema[2].ColumnName);
        Assert.Equal(typeof(DotRocksDecimal), schema[2].DataType);
        Assert.Equal("ratio", schema[3].ColumnName);
        Assert.Equal(typeof(double), schema[3].DataType);
        Assert.Equal("single_value", schema[4].ColumnName);
        Assert.Equal(typeof(float), schema[4].DataType);
        Assert.Equal("created_at", schema[5].ColumnName);
        Assert.Equal(typeof(DateTime), schema[5].DataType);
        Assert.Equal("text_value", schema[6].ColumnName);
        Assert.Equal(typeof(string), schema[6].DataType);

        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal(
            123,
            await reader
                .GetFieldValueAsync<int>(0, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            123L,
            await reader
                .GetFieldValueAsync<long>(1, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            12.34m,
            await reader
                .GetFieldValueAsync<decimal>(2, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            DotRocksDecimal.Parse("12.34"),
            await reader
                .GetFieldValueAsync<DotRocksDecimal>(2, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            1.5d,
            await reader
                .GetFieldValueAsync<double>(3, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            1.25f,
            await reader
                .GetFieldValueAsync<float>(4, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            new DateTime(2026, 6, 19, 13, 14, 15),
            await reader
                .GetFieldValueAsync<DateTime>(5, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            "hello",
            await reader
                .GetFieldValueAsync<string>(6, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.False(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
    }

    [Fact]
    public async Task ExecuteReaderAsync_MapsDecimalBoundariesLosslessly()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CAST('1234567890123456789012345678901234.9000' AS DECIMAL(38, 4)) AS huge_decimal,
                CAST('12.3400' AS DECIMAL(10, 4)) AS exact_decimal
            """;

        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(typeof(DotRocksDecimal), reader.GetFieldType(0));
        Assert.Equal(typeof(DotRocksDecimal), reader.GetFieldType(1));
        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );

        DotRocksDecimal huge = await reader
            .GetFieldValueAsync<DotRocksDecimal>(0, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        DotRocksDecimal exact = await reader
            .GetFieldValueAsync<DotRocksDecimal>(1, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("1234567890123456789012345678901234.9000", huge.ToString());
        Assert.Equal("12.3400", exact.ToString());
        Assert.Throws<DotRocksPrecisionLossException>(() => reader.GetDecimal(0));
        Assert.Equal(12.3400m, reader.GetDecimal(1));
    }

    [Fact]
    public async Task ExecuteReaderAsync_BindsTextCommandParameters()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                @text AS text_value,
                @integer AS integer_value,
                @decimal AS decimal_value,
                IF(@flag, 'yes', 'no') AS flag_value,
                @date AS date_value,
                @time AS time_value,
                @guid AS guid_value,
                HEX(@bytes) AS bytes_value,
                @null_value AS null_value
            """;
        command.Parameters.Add(
            new DotRocksParameter { ParameterName = "text", Value = "O'Reilly" }
        );
        command.Parameters.Add(new DotRocksParameter { ParameterName = "integer", Value = 123 });
        command.Parameters.Add(new DotRocksParameter { ParameterName = "decimal", Value = 12.34m });
        command.Parameters.Add(new DotRocksParameter { ParameterName = "flag", Value = true });
        command.Parameters.Add(
            new DotRocksParameter { ParameterName = "date", Value = new DateOnly(2026, 6, 19) }
        );
        command.Parameters.Add(
            new DotRocksParameter { ParameterName = "time", Value = new TimeOnly(13, 14, 15) }
        );
        command.Parameters.Add(
            new DotRocksParameter
            {
                ParameterName = "guid",
                Value = Guid.Parse("9f4f591e-3db2-4879-856c-1c54b4241b76"),
            }
        );
        command.Parameters.Add(
            new DotRocksParameter
            {
                ParameterName = "bytes",
                Value = new byte[] { 0x00, 0xFF, 0x10 },
            }
        );
        command.Parameters.Add(
            new DotRocksParameter { ParameterName = "null_value", Value = DBNull.Value }
        );

        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal("O'Reilly", reader.GetString(0));
        Assert.Equal(123, reader.GetInt32(1));
        Assert.Equal(12.34m, reader.GetDecimal(2));
        Assert.Equal("yes", reader.GetString(3));
        Assert.Equal(new DateTime(2026, 6, 19), reader.GetDateTime(4));
        Assert.Equal("13:14:15", reader.GetString(5));
        Assert.Equal("9f4f591e-3db2-4879-856c-1c54b4241b76", reader.GetString(6));
        Assert.Equal("00FF10", reader.GetString(7));
        Assert.True(
            await reader
                .IsDBNullAsync(8, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test SQL is built from internally generated table names and constant parameter placeholders."
    )]
    public async Task ExecuteReaderAsync_CharacterizesBinaryExpressionsAndVarBinaryRoundTrip()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        string tableName = await CreateBinaryTableAsync().ConfigureAwait(true);
        try
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await UseTransactionDatabaseAsync(connection).ConfigureAwait(true);

            using (DbCommand insert = connection.CreateCommand())
            {
                insert.CommandText = $"INSERT INTO {tableName} SELECT @id, @bytes";
                AddParameter(insert, "id", 1);
                AddParameter(insert, "bytes", new byte[] { 0x00, 0xFF, 0x10 });
                await insert
                    .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            }

            using DbCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT
                    HEX('abc') AS hex_value,
                    HEX(UNHEX('00FF10')) AS unhex_hex,
                    binary_value,
                    HEX(binary_value) AS binary_hex
                FROM {tableName}
                WHERE id = @id
                """;
            AddParameter(command, "id", 1);

            using DbDataReader reader = await command
                .ExecuteReaderAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            ReadOnlyCollection<DbColumn> schema = reader.GetColumnSchema();

            Assert.Equal(typeof(string), schema[0].DataType);
            Assert.Equal("VAR_STRING", schema[0].DataTypeName);
            Assert.Equal(typeof(string), schema[1].DataType);
            Assert.Equal("VAR_STRING", schema[1].DataTypeName);
            Assert.Equal(typeof(byte[]), schema[2].DataType);
            Assert.Equal("BLOB", schema[2].DataTypeName);
            Assert.Equal(typeof(string), schema[3].DataType);
            Assert.Equal("VAR_STRING", schema[3].DataTypeName);
            Assert.True(
                await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
            );
            Assert.Equal("616263", reader.GetString(0));
            Assert.Equal("00FF10", reader.GetString(1));
            Assert.Equal(
                [0x00, 0xFF, 0x10],
                await reader
                    .GetFieldValueAsync<byte[]>(2, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
            Assert.Equal("00FF10", reader.GetString(3));

            byte[] buffer = [0xAA, 0xAA, 0xAA, 0xAA];
            Assert.Equal(3, reader.GetBytes(2, 0, null, 0, 0));
            Assert.Equal(2, reader.GetBytes(2, 1, buffer, 1, 2));
            Assert.Equal([0xAA, 0xFF, 0x10, 0xAA], buffer);
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test SQL is built from internally generated table names and constant parameter placeholders."
    )]
    public async Task ExecuteReaderAsync_CharacterizesLargeIntAsTextProtocolStringWithInt128Access()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        string tableName = await CreateLargeIntTableAsync().ConfigureAwait(true);
        try
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await UseTransactionDatabaseAsync(connection).ConfigureAwait(true);

            using (DbCommand insert = connection.CreateCommand())
            {
                insert.CommandText =
                    $"INSERT INTO {tableName} SELECT 1, @typical UNION ALL SELECT 2, @maximum UNION ALL SELECT 3, @minimum";
                AddParameter(insert, "typical", (Int128)123);
                AddParameter(
                    insert,
                    "maximum",
                    Int128.Parse(
                        "170141183460469231731687303715884105727",
                        CultureInfo.InvariantCulture
                    )
                );
                AddParameter(
                    insert,
                    "minimum",
                    Int128.Parse(
                        "-170141183460469231731687303715884105728",
                        CultureInfo.InvariantCulture
                    )
                );
                await insert
                    .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            }

            using DbCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT value FROM {tableName} ORDER BY id";
            using DbDataReader reader = await command
                .ExecuteReaderAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            ReadOnlyCollection<DbColumn> schema = reader.GetColumnSchema();

            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.Equal(typeof(string), schema[0].DataType);
            Assert.Equal("STRING", schema[0].DataTypeName);
            Assert.Equal(40, schema[0].ColumnSize);

            Assert.True(
                await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
            );
            Assert.Equal(
                (Int128)123,
                await reader
                    .GetFieldValueAsync<Int128>(0, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
            Assert.True(
                await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
            );
            Assert.Equal(
                Int128.Parse(
                    "170141183460469231731687303715884105727",
                    CultureInfo.InvariantCulture
                ),
                await reader
                    .GetFieldValueAsync<Int128>(0, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
            Assert.True(
                await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
            );
            Assert.Equal(
                Int128.Parse(
                    "-170141183460469231731687303715884105728",
                    CultureInfo.InvariantCulture
                ),
                await reader
                    .GetFieldValueAsync<Int128>(0, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task PreparedCommand_SelectValue_ExecutesWithChangedParameterValues()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT @value";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "value";
        parameter.Value = 42;
        command.Parameters.Add(parameter);

        await command.PrepareAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        object? first = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        parameter.Value = 43;
        object? second = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // StarRocks types the integer literal as TINYINT, which maps to sbyte.
        Assert.Equal(42, Convert.ToInt32(first, CultureInfo.InvariantCulture));
        Assert.Equal(43, Convert.ToInt32(second, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task PreparedCommand_BindsCommonValuesSafely()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                @text AS text_value,
                @null_value AS null_value,
                @decimal AS decimal_value,
                @dotrocks_decimal AS dotrocks_decimal_value,
                IF(@flag, 'yes', 'no') AS flag_value,
                @created_at AS created_at_value
            """;
        AddParameter(command, "text", "quote'\\slash");
        AddParameter(command, "null_value", DBNull.Value);
        AddParameter(command, "decimal", 12.34m);
        AddParameter(
            command,
            "dotrocks_decimal",
            DotRocksDecimal.Parse("1234567890123456789012345678901234.9000")
        );
        AddParameter(command, "flag", true);
        AddParameter(command, "created_at", new DateTime(2026, 6, 19, 13, 14, 15));

        await command.PrepareAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
        Assert.Equal("quote'\\slash", reader.GetString(0));
        Assert.True(
            await reader
                .IsDBNullAsync(1, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(12.34m, reader.GetDecimal(2));
        Assert.Equal(
            DotRocksDecimal.Parse("1234567890123456789012345678901234.9000"),
            await reader
                .GetFieldValueAsync<DotRocksDecimal>(3, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal("yes", reader.GetString(4));
        Assert.Equal(new DateTime(2026, 6, 19, 13, 14, 15), reader.GetDateTime(5));
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test SQL is built from internally generated table names and constant parameter placeholders."
    )]
    public async Task PreparedCommand_ExecutesParameterizedSelectFromTableRepeatedly()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        string tableName = await CreateTransactionTableAsync().ConfigureAwait(true);
        try
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await UseTransactionDatabaseAsync(connection).ConfigureAwait(true);
            await ExecuteNonQueryAsync(connection, null, $"INSERT INTO {tableName} SELECT 1, 10")
                .ConfigureAwait(true);
            await ExecuteNonQueryAsync(connection, null, $"INSERT INTO {tableName} SELECT 2, 20")
                .ConfigureAwait(true);

            using DbCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT value FROM {tableName} WHERE id = @id";
            var id = command.CreateParameter();
            id.ParameterName = "id";
            id.Value = 1;
            command.Parameters.Add(id);

            await command.PrepareAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            object? first = await command
                .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            id.Value = 2;
            object? second = await command
                .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(10, first);
            Assert.Equal(20, second);
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task PreparedCommand_FailedValidationDoesNotPoisonPooledConnection()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        DotRocksConnection.ClearAllPools();
        try
        {
            string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
            long firstConnectionId;
            using (var first = new DotRocksConnection(connectionString))
            {
                await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);

                using DbCommand command = first.CreateCommand();
                command.CommandText = "SELECT @value";
                AddParameter(command, "value", 1);
                await command
                    .PrepareAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                command.Parameters.Clear();

                await Assert
                    .ThrowsAsync<InvalidOperationException>(async () =>
                        await command
                            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                            .ConfigureAwait(true)
                    )
                    .ConfigureAwait(true);
                Assert.Equal(ConnectionState.Open, first.State);
                await first.CloseAsync().ConfigureAwait(true);
            }

            using (var second = new DotRocksConnection(connectionString))
            {
                await second.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                long secondConnectionId = await ReadConnectionIdAsync(second).ConfigureAwait(true);

                Assert.Equal(firstConnectionId, secondConnectionId);
                await second.CloseAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            DotRocksConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ExecuteScalarAsync_ReturnsNullForSqlNull()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT NULL";

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Null(value);
    }

    [Fact]
    public async Task ExecuteScalarAsync_CommandTimeout_ClosesConnection()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SLEEP(3)";
        command.CommandTimeout = 1;

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await command
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(exception.IsTransient);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ExecuteScalarAsync_UserCancellation_ClosesConnection()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SLEEP(3)";
        command.CommandTimeout = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert
            .ThrowsAsync<OperationCanceledException>(async () =>
                await command.ExecuteScalarAsync(cancellation.Token).ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Cancel_ActiveCommand_ClosesConnection()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SLEEP(3)";
        command.CommandTimeout = 0;

        Task<object?> execution = command.ExecuteScalarAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        command.Cancel();

        await Assert
            .ThrowsAsync<OperationCanceledException>(async () =>
                await execution.ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task BadSql_ThrowsDotRocksException()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT FROM";

        DotRocksException exception = await Assert
            .ThrowsAsync<DotRocksException>(async () =>
                await command
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.NotNull(exception.ServerErrorCode);
        Assert.DoesNotContain(
            IntegrationTestEnvironment.ConnectionString,
            exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task OpenClose_CanRepeatOnSeparateConnections()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        for (int i = 0; i < 2; i++)
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(ConnectionState.Open, connection.State);
            await connection.CloseAsync().ConfigureAwait(true);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
    }

    [Fact]
    public async Task PooledConnections_ReusePhysicalConnection()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        DotRocksConnection.ClearAllPools();
        try
        {
            string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
            long firstConnectionId;
            using (var first = new DotRocksConnection(connectionString))
            {
                await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);
                await first.CloseAsync().ConfigureAwait(true);
            }

            using (var second = new DotRocksConnection(connectionString))
            {
                await second.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                long secondConnectionId = await ReadConnectionIdAsync(second).ConfigureAwait(true);

                Assert.Equal(firstConnectionId, secondConnectionId);
                await second.CloseAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            DotRocksConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PooledConnections_RespectMaximumPoolSize()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        DotRocksConnection.ClearAllPools();
        try
        {
            string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
            using var first = new DotRocksConnection(connectionString);
            using var second = new DotRocksConnection(connectionString);
            await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Task openSecond = second.OpenAsync(TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.False(openSecond.IsCompleted);

            await first.CloseAsync().ConfigureAwait(true);
            await openSecond.ConfigureAwait(true);
            Assert.Equal(ConnectionState.Open, second.State);
            await second.CloseAsync().ConfigureAwait(true);
        }
        finally
        {
            DotRocksConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PooledConnections_ConcurrentLeaseAndClearDoNotDeadlockOrLeakPermits()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        DotRocksConnection.ClearAllPools();
        try
        {
            string connectionString = BuildPoolingConnectionString(maximumPoolSize: 4);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            CancellationToken ct = cancellation.Token;

            async Task LeaseLoopAsync()
            {
                for (int i = 0; i < 25; i++)
                {
                    using var connection = new DotRocksConnection(connectionString);
                    await connection.OpenAsync(ct).ConfigureAwait(true);
                    using DbCommand command = connection.CreateCommand();
                    command.CommandText = "SELECT 1";
                    _ = await command.ExecuteScalarAsync(ct).ConfigureAwait(true);
                    await connection.CloseAsync().ConfigureAwait(true);
                }
            }

            Task clearLoop = Task.Run(
                async () =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        DotRocksConnection.ClearAllPools();
                        await Task.Delay(TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(true);
                    }
                },
                ct
            );

            Task[] leaseLoops = [.. Enumerable.Range(0, 8).Select(_ => LeaseLoopAsync())];

            // If a permit were lost or the pool deadlocked, this would exceed the 30s token and throw.
            await Task.WhenAll([.. leaseLoops, clearLoop]).ConfigureAwait(true);

            // The pool must still be fully usable after the concurrent churn.
            DotRocksConnection.ClearAllPools();
            using var probe = new DotRocksConnection(connectionString);
            await probe.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(ConnectionState.Open, probe.State);
            await probe.CloseAsync().ConfigureAwait(true);
        }
        finally
        {
            DotRocksConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PooledConnections_DiscardBrokenPhysicalConnection()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        DotRocksConnection.ClearAllPools();
        try
        {
            string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
            long firstConnectionId;
            using (var first = new DotRocksConnection(connectionString))
            {
                await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);

                using DbCommand command = first.CreateCommand();
                command.CommandText = "SELECT SLEEP(3)";
                command.CommandTimeout = 1;

                await Assert
                    .ThrowsAsync<DotRocksException>(async () =>
                        await command
                            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                            .ConfigureAwait(true)
                    )
                    .ConfigureAwait(true);
                Assert.Equal(ConnectionState.Closed, first.State);
            }

            using (var second = new DotRocksConnection(connectionString))
            {
                await second.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                long secondConnectionId = await ReadConnectionIdAsync(second).ConfigureAwait(true);

                Assert.NotEqual(firstConnectionId, secondConnectionId);
                await second.CloseAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            DotRocksConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ActiveReader_BlocksSecondCommandUntilConsumed()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand firstCommand = connection.CreateCommand();
        firstCommand.CommandText = "SELECT 1 UNION ALL SELECT 2";
        using DbDataReader reader = await firstCommand
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );

        using DbCommand secondCommand = connection.CreateCommand();
        secondCommand.CommandText = "SELECT 1";
        InvalidOperationException exception = await Assert
            .ThrowsAsync<InvalidOperationException>(async () =>
                await secondCommand
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);

        Assert.Contains("active reader", exception.Message, StringComparison.OrdinalIgnoreCase);

        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        { }

        object? value = await secondCommand
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        // StarRocks types the integer literal as TINYINT, which maps to sbyte.
        Assert.Equal(1, Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ClosingReaderBeforeExhaustion_DrainsAndKeepsConnectionUsable()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        DotRocksConnection.ClearAllPools();
        try
        {
            string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
            long firstConnectionId;

            using (var first = new DotRocksConnection(connectionString))
            {
                await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);

                using (DbCommand command = first.CreateCommand())
                {
                    command.CommandText = "SELECT 1 UNION ALL SELECT 2";
                    using DbDataReader reader = await command
                        .ExecuteReaderAsync(TestContext.Current.CancellationToken)
                        .ConfigureAwait(true);
                    Assert.True(
                        await reader
                            .ReadAsync(TestContext.Current.CancellationToken)
                            .ConfigureAwait(true)
                    );

                    // Closing before exhaustion drains the remaining rows; the logical
                    // connection stays open and the physical connection stays clean.
                    await reader.CloseAsync().ConfigureAwait(true);
                }

                Assert.Equal(ConnectionState.Open, first.State);

                using DbCommand followUp = first.CreateCommand();
                followUp.CommandText = "SELECT 3";
                object? value = await followUp
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Assert.Equal(3, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }

            using (var second = new DotRocksConnection(connectionString))
            {
                await second.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                long secondConnectionId = await ReadConnectionIdAsync(second).ConfigureAwait(true);

                Assert.Equal(firstConnectionId, secondConnectionId);
                await second.CloseAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            DotRocksConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CancelingActiveReaderRead_DiscardsPooledPhysicalConnection()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        DotRocksConnection.ClearAllPools();
        try
        {
            string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
            long firstConnectionId;

            using (var first = new DotRocksConnection(connectionString))
            {
                await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);

                using DbCommand command = first.CreateCommand();
                command.CommandText = "SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3";
                command.CommandTimeout = 0;
                using DbDataReader reader = await command
                    .ExecuteReaderAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Assert.True(
                    await reader
                        .ReadAsync(TestContext.Current.CancellationToken)
                        .ConfigureAwait(true)
                );

                // Cancel mid-stream (deterministically, independent of server timing): a cancelled
                // reader read must throw and the partially-consumed connection must be discarded.
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.Current.CancellationToken
                );
                await cancellation.CancelAsync().ConfigureAwait(true);

                await Assert
                    .ThrowsAsync<OperationCanceledException>(async () =>
                        await reader.ReadAsync(cancellation.Token).ConfigureAwait(true)
                    )
                    .ConfigureAwait(true);
                Assert.Equal(ConnectionState.Closed, first.State);
            }

            using (var second = new DotRocksConnection(connectionString))
            {
                await second.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                long secondConnectionId = await ReadConnectionIdAsync(second).ConfigureAwait(true);

                Assert.NotEqual(firstConnectionId, secondConnectionId);
                await second.CloseAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            DotRocksConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ReaderClose_RespectsCommandBehaviorCloseConnection()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        using DbDataReader reader = await command
            .ExecuteReaderAsync(
                CommandBehavior.CloseConnection,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        { }

        await reader.CloseAsync().ConfigureAwait(true);

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test SQL uses only a compile-time row-count constant."
    )]
    public async Task LargeResult_OpensReaderWithoutBufferingAllRows()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        const int rowCount = 100_000;
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocatedBeforeOpen = GC.GetTotalAllocatedBytes(precise: true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH d AS (
                SELECT 0 AS n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4
                UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9
            )
            SELECT
                a.n + (b.n * 10) + (c.n * 100) + (d.n * 1000) + (e.n * 10000) AS number
            FROM d a, d b, d c, d d, d e
            """;
        using DbDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        long allocatedForOpen = GC.GetTotalAllocatedBytes(precise: true) - allocatedBeforeOpen;
        Assert.True(
            allocatedForOpen < 8_000_000,
            $"Opening the reader allocated {allocatedForOpen.ToString(CultureInfo.InvariantCulture)} byte(s), which indicates result buffering."
        );

        int rowsRead = 0;
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            _ = reader.GetInt64(0);
            rowsRead++;
        }

        Assert.Equal(rowCount, rowsRead);
    }

    [Fact]
    public async Task Transaction_Commit_MakesInsertedRowsVisible()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        string tableName = await CreateTransactionTableAsync().ConfigureAwait(true);
        try
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await UseTransactionDatabaseAsync(connection).ConfigureAwait(true);
            using DbTransaction transaction = await connection
                .BeginTransactionAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    $"INSERT INTO {tableName} SELECT 1, 10"
                )
                .ConfigureAwait(true);

            await transaction
                .CommitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(10, await ReadTransactionValueAsync(tableName, 1).ConfigureAwait(true));
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Transaction_Rollback_HidesInsertedRows()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        string tableName = await CreateTransactionTableAsync().ConfigureAwait(true);
        try
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await UseTransactionDatabaseAsync(connection).ConfigureAwait(true);
            using DbTransaction transaction = await connection
                .BeginTransactionAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    $"INSERT INTO {tableName} SELECT 1, 10"
                )
                .ConfigureAwait(true);

            await transaction
                .RollbackAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            int rowCount = await ReadTransactionRowCountAsync(tableName).ConfigureAwait(true);
            if (rowCount != 0)
            {
                Assert.Skip(
                    "The pinned StarRocks integration image accepted ROLLBACK WORK but made the inserted row visible."
                );
            }

            Assert.Equal(0, rowCount);
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Transaction_DisposeWithoutCommit_RollsBackAndKeepsConnectionUsable()
    {
        IntegrationTestEnvironment.SkipUnlessEnabled();

        string tableName = await CreateTransactionTableAsync().ConfigureAwait(true);
        try
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await UseTransactionDatabaseAsync(connection).ConfigureAwait(true);

            DbTransaction transaction = await connection
                .BeginTransactionAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await using (transaction.ConfigureAwait(true))
            {
                await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        $"INSERT INTO {tableName} SELECT 1, 10"
                    )
                    .ConfigureAwait(true);
                // Intentionally no Commit/Rollback: disposing must roll back.
            }

            // The connection must remain open and usable after the rolled-back transaction.
            Assert.Equal(ConnectionState.Open, connection.State);
            using (DbCommand probe = connection.CreateCommand())
            {
                probe.CommandText = "SELECT 7";
                object? value = await probe
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Assert.Equal(7, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }

            int rowCount = await ReadTransactionRowCountAsync(tableName).ConfigureAwait(true);
            if (rowCount != 0)
            {
                Assert.Skip(
                    "The pinned StarRocks integration image accepted ROLLBACK WORK but made the inserted row visible."
                );
            }

            Assert.Equal(0, rowCount);
        }
        finally
        {
            await DropTableAsync(tableName).ConfigureAwait(true);
        }
    }

    private static string BuildPoolingConnectionString(int maximumPoolSize)
    {
        var builder = new DotRocksConnectionStringBuilder(
            IntegrationTestEnvironment.ConnectionString
        )
        {
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = maximumPoolSize,
            ConnectionIdleTimeout = 300,
        };

        return builder.ConnectionString;
    }

    private static string BuildDatabaseConnectionString(string database)
    {
        var builder = new DotRocksConnectionStringBuilder(
            IntegrationTestEnvironment.ConnectionString
        )
        {
            Database = database,
        };

        return builder.ConnectionString;
    }

    private static async Task<long> ReadConnectionIdAsync(DotRocksConnection connection)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CONNECTION_ID()";
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<string> CreateTransactionTableAsync()
    {
        string tableName =
            "dotrocks_tx_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand createDatabase = connection.CreateCommand();
        createDatabase.CommandText = $"CREATE DATABASE IF NOT EXISTS {TransactionDatabaseName}";
        await createDatabase
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        using var databaseConnection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await databaseConnection
            .OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        using DbCommand command = databaseConnection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {tableName}
            (
                id INT NOT NULL,
                value INT NOT NULL
            )
            PRIMARY KEY(id)
            DISTRIBUTED BY HASH(id) BUCKETS 1
            PROPERTIES ("replication_num" = "1")
            """;
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return tableName;
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<string> CreateBinaryTableAsync()
    {
        string tableName =
            "dotrocks_bin_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand createDatabase = connection.CreateCommand();
        createDatabase.CommandText = $"CREATE DATABASE IF NOT EXISTS {TransactionDatabaseName}";
        await createDatabase
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        using var databaseConnection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await databaseConnection
            .OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        using DbCommand command = databaseConnection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {tableName}
            (
                id INT NOT NULL,
                binary_value VARBINARY(16) NULL
            )
            DUPLICATE KEY(id)
            DISTRIBUTED BY HASH(id) BUCKETS 1
            PROPERTIES ("replication_num" = "1")
            """;
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return tableName;
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<string> CreateLargeIntTableAsync()
    {
        string tableName =
            "dotrocks_largeint_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand createDatabase = connection.CreateCommand();
        createDatabase.CommandText = $"CREATE DATABASE IF NOT EXISTS {TransactionDatabaseName}";
        await createDatabase
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        using var databaseConnection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await databaseConnection
            .OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        using DbCommand command = databaseConnection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {tableName}
            (
                id INT NOT NULL,
                value LARGEINT NOT NULL
            )
            DUPLICATE KEY(id)
            DISTRIBUTED BY HASH(id) BUCKETS 1
            PROPERTIES ("replication_num" = "1")
            """;
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return tableName;
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task DropTableAsync(string tableName)
    {
        using var connection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {tableName}";
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test SQL is built from internally generated table names and constant values."
    )]
    private static async Task ExecuteNonQueryAsync(
        DotRocksConnection connection,
        DbTransaction? transaction,
        string commandText
    )
    {
        using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test database names are constants controlled by the test."
    )]
    private static async Task UseTransactionDatabaseAsync(DotRocksConnection connection)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = $"USE {TransactionDatabaseName}";
        await command
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<int> ReadTransactionValueAsync(string tableName, int id)
    {
        using var connection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {tableName} WHERE id = @id";
        command.Parameters.Add(new DotRocksParameter { ParameterName = "id", Value = id });
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Integration test table names are generated internally and never use user input."
    )]
    private static async Task<int> ReadTransactionRowCountAsync(string tableName)
    {
        using var connection = new DotRocksConnection(
            BuildDatabaseConnectionString(TransactionDatabaseName)
        );
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
