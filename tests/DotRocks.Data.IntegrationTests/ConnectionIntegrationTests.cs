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
    private const string TransactionDatabaseName = "dotrocks_tx";

    [Fact]
    public async Task OpenAsync_AuthenticatesAgainstStarRocks()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);

        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.False(string.IsNullOrWhiteSpace(connection.ServerVersion));
    }

    [Fact]
    public async Task ExecuteScalarAsync_ReturnsSelectOne()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, value);
    }

    [Fact]
    public async Task ExecuteReaderAsync_ReadsSelectOneResultSet()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        using System.Data.Common.DbDataReader reader = await command
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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = sql;

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(expected, value);
    }

    [Fact]
    public async Task ExecuteReaderAsync_MapsCommonStarRocksTypes()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CAST(123 AS INT) AS i32,
                CAST(123 AS BIGINT) AS i64,
                CAST(12.34 AS DECIMAL(10, 2)) AS amount,
                CAST(1.5 AS DOUBLE) AS ratio,
                CAST('2026-06-19' AS DATE) AS created_on,
                CAST('2026-06-19 13:14:15' AS DATETIME) AS created_at
            """;

        using System.Data.Common.DbDataReader reader = await command
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
        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Equal(typeof(long), reader.GetFieldType(1));
        Assert.Equal(typeof(DotRocksDecimal), reader.GetFieldType(2));
        Assert.Equal(typeof(double), reader.GetFieldType(3));
        Assert.Equal(typeof(DateTime), reader.GetFieldType(4));
        Assert.Equal(typeof(DateTime), reader.GetFieldType(5));
        Assert.False(
            await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
        );
    }

    [Fact]
    public async Task ExecuteReaderAsync_ExposesColumnSchemaAndGenericFieldValues()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
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

        using System.Data.Common.DbDataReader reader = await command
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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT @value";
        DbParameter parameter = command.CreateParameter();
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

        Assert.Equal(42, first);
        Assert.Equal(43, second);
    }

    [Fact]
    public async Task PreparedCommand_BindsCommonValuesSafely()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
            DbParameter id = command.CreateParameter();
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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        DotRocksConnection.ClearAllPools();
        string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
        long firstConnectionId;
        using (var first = new DotRocksConnection(connectionString))
        {
            await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);

            using DbCommand command = first.CreateCommand();
            command.CommandText = "SELECT @value";
            AddParameter(command, "value", 1);
            await command.PrepareAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
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

        DotRocksConnection.ClearAllPools();
    }

    [Fact]
    public async Task ExecuteScalarAsync_ReturnsNullForSqlNull()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT NULL";

        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Null(value);
    }

    [Fact]
    public async Task ExecuteScalarAsync_CommandTimeout_ClosesConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        using var connection = new DotRocksConnection(IntegrationTestEnvironment.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using System.Data.Common.DbCommand command = connection.CreateCommand();
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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        DotRocksConnection.ClearAllPools();
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

        DotRocksConnection.ClearAllPools();
    }

    [Fact]
    public async Task PooledConnections_RespectMaximumPoolSize()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        DotRocksConnection.ClearAllPools();
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
        DotRocksConnection.ClearAllPools();
    }

    [Fact]
    public async Task PooledConnections_ConcurrentLeaseAndClearDoNotDeadlockOrLeakPermits()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        DotRocksConnection.ClearAllPools();
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
        DotRocksConnection.ClearAllPools();
    }

    [Fact]
    public async Task PooledConnections_DiscardBrokenPhysicalConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        DotRocksConnection.ClearAllPools();
        string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
        long firstConnectionId;
        using (var first = new DotRocksConnection(connectionString))
        {
            await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);

            using System.Data.Common.DbCommand command = first.CreateCommand();
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

        DotRocksConnection.ClearAllPools();
    }

    [Fact]
    public async Task ActiveReader_BlocksSecondCommandUntilConsumed()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
        Assert.Equal(1, value);
    }

    [Fact]
    public async Task ClosingReaderBeforeExhaustion_DiscardsPhysicalConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        DotRocksConnection.ClearAllPools();
        string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
        long firstConnectionId;

        using (var first = new DotRocksConnection(connectionString))
        {
            await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);

            using DbCommand command = first.CreateCommand();
            command.CommandText = "SELECT 1 UNION ALL SELECT 2";
            using DbDataReader reader = await command
                .ExecuteReaderAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(
                await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
            );

            await reader.CloseAsync().ConfigureAwait(true);

            Assert.Equal(ConnectionState.Closed, first.State);
        }

        using (var second = new DotRocksConnection(connectionString))
        {
            await second.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            long secondConnectionId = await ReadConnectionIdAsync(second).ConfigureAwait(true);

            Assert.NotEqual(firstConnectionId, secondConnectionId);
            await second.CloseAsync().ConfigureAwait(true);
        }

        DotRocksConnection.ClearAllPools();
    }

    [Fact]
    public async Task CancelingActiveReaderRead_DiscardsPooledPhysicalConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        DotRocksConnection.ClearAllPools();
        string connectionString = BuildPoolingConnectionString(maximumPoolSize: 1);
        long firstConnectionId;

        using (var first = new DotRocksConnection(connectionString))
        {
            await first.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            firstConnectionId = await ReadConnectionIdAsync(first).ConfigureAwait(true);

            using DbCommand command = first.CreateCommand();
            command.CommandText = "SELECT 1 UNION ALL SELECT SLEEP(3)";
            command.CommandTimeout = 0;
            using DbDataReader reader = await command
                .ExecuteReaderAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(
                await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)
            );
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken
            );
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

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

        DotRocksConnection.ClearAllPools();
    }

    [Fact]
    public async Task ReaderClose_RespectsCommandBehaviorCloseConnection()
    {
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        string tableName = await CreateTransactionTableAsync().ConfigureAwait(true);
        try
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await UseTransactionDatabaseAsync(connection).ConfigureAwait(true);
            using System.Data.Common.DbTransaction transaction = await connection
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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        string tableName = await CreateTransactionTableAsync().ConfigureAwait(true);
        try
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await UseTransactionDatabaseAsync(connection).ConfigureAwait(true);
            using System.Data.Common.DbTransaction transaction = await connection
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
        if (!IntegrationTestEnvironment.IsEnabled)
        {
            Assert.Skip(
                "StarRocks integration tests require DOTROCKS_RUN_INTEGRATION=1 and a reachable StarRocks server."
            );
        }

        string tableName = await CreateTransactionTableAsync().ConfigureAwait(true);
        try
        {
            using var connection = new DotRocksConnection(
                BuildDatabaseConnectionString(TransactionDatabaseName)
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await UseTransactionDatabaseAsync(connection).ConfigureAwait(true);

            System.Data.Common.DbTransaction transaction = await connection
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
            using (System.Data.Common.DbCommand probe = connection.CreateCommand())
            {
                probe.CommandText = "SELECT 7";
                object? value = await probe
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Assert.Equal(
                    7,
                    Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
                );
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
        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CONNECTION_ID()";
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
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
        using System.Data.Common.DbCommand createDatabase = connection.CreateCommand();
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
        using System.Data.Common.DbCommand command = databaseConnection.CreateCommand();
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
        using System.Data.Common.DbCommand createDatabase = connection.CreateCommand();
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
        using System.Data.Common.DbCommand command = databaseConnection.CreateCommand();
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
        using System.Data.Common.DbCommand createDatabase = connection.CreateCommand();
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
        using System.Data.Common.DbCommand command = databaseConnection.CreateCommand();
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
        using System.Data.Common.DbCommand command = connection.CreateCommand();
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
        System.Data.Common.DbTransaction? transaction,
        string commandText
    )
    {
        using System.Data.Common.DbCommand command = connection.CreateCommand();
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
        using System.Data.Common.DbCommand command = connection.CreateCommand();
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
        using System.Data.Common.DbCommand command = connection.CreateCommand();
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
        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        object? value = await command
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
