using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotRocks.Data.Protocol.Results;

namespace DotRocks.Data;

/// <summary>
/// Reads rows returned by a DotRocks command.
/// </summary>
public sealed class DotRocksDataReader
    : DbDataReader,
        IEnumerable<IDataRecord>,
        IDbColumnSchemaGenerator
{
    private const ushort NotNullColumnFlag = 0x0001;
    private readonly QueryResult? _bufferedResult;
    private readonly StreamingQueryResult? _streamingResult;
    private readonly TextResultRowReader? _rowReader;
    private readonly IReadOnlyList<ColumnDefinition> _columns;
    private readonly DotRocksConnection? _connection;
    private readonly CommandBehavior _behavior;
    private ReadOnlyCollection<DbColumn>? _columnSchema;
    private object?[]? _currentRow;
    private int _rowIndex = -1;
    private bool _isClosed;
    private bool _isConsumed;
    private bool _connectionCompletionReported;

    internal DotRocksDataReader(
        QueryResult result,
        DotRocksConnection? connection = null,
        CommandBehavior behavior = CommandBehavior.Default
    )
    {
        _bufferedResult = result;
        _columns = result.Columns;
        _connection = connection;
        _behavior = behavior;
    }

    internal DotRocksDataReader(
        StreamingQueryResult result,
        DotRocksConnection? connection = null,
        CommandBehavior behavior = CommandBehavior.Default
    )
    {
        _streamingResult = result;
        _rowReader = result.RowReader;
        _columns = result.Columns;
        _connection = connection;
        _behavior = behavior;
        _isConsumed = !result.HasResultSet;
    }

    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc />
    public override int Depth => 0;

    /// <inheritdoc />
    public override int FieldCount => _columns.Count;

    /// <inheritdoc />
    public override bool HasRows =>
        _bufferedResult is not null ? _bufferedResult.Rows.Count > 0 : FieldCount > 0;

    /// <inheritdoc />
    public override bool IsClosed => _isClosed;

    /// <inheritdoc />
    public override int RecordsAffected =>
        RecordsAffectedCore > int.MaxValue ? int.MaxValue : (int)RecordsAffectedCore;

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) =>
        Convert.ToBoolean(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override byte GetByte(int ordinal) =>
        Convert.ToByte(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override long GetBytes(
        int ordinal,
        long dataOffset,
        byte[]? buffer,
        int bufferOffset,
        int length
    ) => throw new NotSupportedException("Binary streaming is not implemented yet.");

    /// <inheritdoc />
    public override char GetChar(int ordinal) =>
        Convert.ToChar(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override long GetChars(
        int ordinal,
        long dataOffset,
        char[]? buffer,
        int bufferOffset,
        int length
    ) => throw new NotSupportedException("Character streaming is not implemented yet.");

    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return ColumnTypeMapper.GetDataTypeName(_columns[ordinal].ColumnType);
    }

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal) =>
        Convert.ToDateTime(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal)
    {
        object value = GetNonNullValue(ordinal);
        return value is DotRocksDecimal dotRocksDecimal
            ? dotRocksDecimal.ToDecimal()
            : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override double GetDouble(int ordinal) =>
        Convert.ToDouble(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override Type GetFieldType(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return ColumnTypeMapper.GetFieldType(_columns[ordinal].ColumnType);
    }

    /// <inheritdoc />
    public override T GetFieldValue<T>(int ordinal) => (T)GetTypedFieldValue(ordinal, typeof(T))!;

    /// <inheritdoc />
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetFieldValue<T>(ordinal));
    }

    /// <inheritdoc />
    public override float GetFloat(int ordinal) =>
        Convert.ToSingle(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));

    /// <inheritdoc />
    public override short GetInt16(int ordinal) =>
        Convert.ToInt16(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override int GetInt32(int ordinal) =>
        Convert.ToInt32(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override long GetInt64(int ordinal) =>
        Convert.ToInt64(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string GetName(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return _columns[ordinal].Name;
    }

    /// <inheritdoc />
    [SuppressMessage(
        "Usage",
        "CA2201:Do not raise reserved exception types",
        Justification = "DbDataReader.GetOrdinal conventionally reports a missing column with IndexOutOfRangeException."
    )]
    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int i = 0; i < _columns.Count; i++)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(_columns[i].Name, name))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException($"Column '{name}' was not found.");
    }

    /// <inheritdoc />
    public override string GetString(int ordinal) =>
        Convert.ToString(GetNonNullValue(ordinal), CultureInfo.InvariantCulture)
        ?? throw new InvalidCastException("Column value cannot be converted to string.");

    /// <inheritdoc />
    public override object GetValue(int ordinal)
    {
        ValidateReadableRow();
        ValidateOrdinal(ordinal);
        object?[] row = _currentRow!;
        return row[ordinal] ?? DBNull.Value;
    }

    /// <inheritdoc />
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    /// <inheritdoc />
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) == DBNull.Value;

    /// <inheritdoc />
    public ReadOnlyCollection<DbColumn> GetColumnSchema()
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }

        return _columnSchema ??= BuildColumnSchema();
    }

    /// <inheritdoc />
    public override Task<ReadOnlyCollection<DbColumn>> GetColumnSchemaAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetColumnSchema());
    }

    /// <inheritdoc />
    public override bool NextResult() => false;

    /// <inheritdoc />
    public override bool Read()
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }

        if (_bufferedResult is not null)
        {
            if (_rowIndex + 1 >= _bufferedResult.Rows.Count)
            {
                _isConsumed = true;
                ReportConnectionCompletion(reusable: true);
                return false;
            }

            _rowIndex++;
            _currentRow = _bufferedResult.Rows[_rowIndex];
            return true;
        }

        return ReadStreamingRowAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }

        if (_bufferedResult is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Read();
        }

        return await ReadStreamingRowAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReadStreamingRowAsync(CancellationToken cancellationToken)
    {
        if (_isConsumed)
        {
            return false;
        }

        if (_rowReader is null)
        {
            _isConsumed = true;
            ReportConnectionCompletion(reusable: true);
            return false;
        }

        try
        {
            object?[]? row = await _rowReader.ReadRowAsync(cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                _isConsumed = true;
                _currentRow = null;
                ReportConnectionCompletion(reusable: true);
                return false;
            }

            _rowIndex++;
            _currentRow = row;
            return true;
        }
        catch
        {
            _isConsumed = true;
            ReportConnectionCompletion(reusable: false);
            throw;
        }
    }

    /// <inheritdoc />
    public override IEnumerator GetEnumerator()
    {
        while (Read())
        {
            yield return this;
        }
    }

    IEnumerator<IDataRecord> IEnumerable<IDataRecord>.GetEnumerator()
    {
        while (Read())
        {
            yield return this;
        }
    }

    /// <inheritdoc />
    public override void Close()
    {
        if (_isClosed)
        {
            return;
        }

        bool reusable =
            _isConsumed || _bufferedResult is not null || _streamingResult?.HasResultSet != true;
        _isClosed = true;
        _isConsumed = true;
        ReportConnectionCompletion(reusable);
        if ((_behavior & CommandBehavior.CloseConnection) != 0)
        {
            _connection?.Close();
        }
    }

    internal void MarkCompletedBecauseConnectionClosed()
    {
        _connectionCompletionReported = true;
        _isClosed = true;
        _isConsumed = true;
    }

    private ReadOnlyCollection<DbColumn> BuildColumnSchema()
    {
        var columns = new DbColumn[_columns.Count];
        for (int i = 0; i < _columns.Count; i++)
        {
            ColumnDefinition definition = _columns[i];
            columns[i] = new DotRocksDbColumn(
                definition.Name,
                i,
                ColumnTypeMapper.GetFieldType(definition.ColumnType),
                ColumnTypeMapper.GetDataTypeName(definition.ColumnType),
                (definition.Flags & NotNullColumnFlag) == 0,
                definition.Catalog.Length == 0 ? null : definition.Catalog,
                definition.Schema.Length == 0 ? null : definition.Schema,
                definition.Table.Length == 0 ? null : definition.Table,
                definition.OriginalName.Length == 0 ? null : definition.OriginalName,
                definition.ColumnLength > int.MaxValue ? int.MaxValue : (int)definition.ColumnLength
            );
        }

        return Array.AsReadOnly(columns);
    }

    private object? GetTypedFieldValue(int ordinal, Type requestedType)
    {
        ArgumentNullException.ThrowIfNull(requestedType);
        object value = GetValue(ordinal);
        if (value == DBNull.Value)
        {
            return GetDbNullFieldValue(requestedType);
        }

        Type targetType = Nullable.GetUnderlyingType(requestedType) ?? requestedType;
        try
        {
            object converted = GetNonNullTypedFieldValue(ordinal, targetType, value);
            return converted;
        }
        catch (Exception ex)
            when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidCastException(
                $"Column value cannot be converted to {requestedType.Name}.",
                ex
            );
        }
    }

    private static DBNull? GetDbNullFieldValue(Type requestedType)
    {
        if (requestedType == typeof(DBNull) || requestedType == typeof(object))
        {
            return DBNull.Value;
        }

        if (Nullable.GetUnderlyingType(requestedType) is not null)
        {
            return null;
        }

        throw new InvalidCastException("Column value is NULL.");
    }

    private object GetNonNullTypedFieldValue(int ordinal, Type targetType, object value)
    {
        if (targetType == typeof(object))
        {
            return value;
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (targetType == typeof(int))
        {
            return GetInt32(ordinal);
        }

        if (targetType == typeof(long))
        {
            return GetInt64(ordinal);
        }

        if (targetType == typeof(decimal))
        {
            return GetDecimal(ordinal);
        }

        if (targetType == typeof(DotRocksDecimal) && value is decimal decimalValue)
        {
            return DotRocksDecimal.FromDecimal(decimalValue);
        }

        if (targetType == typeof(double))
        {
            return GetDouble(ordinal);
        }

        if (targetType == typeof(float))
        {
            return GetFloat(ordinal);
        }

        if (targetType == typeof(string))
        {
            return GetString(ordinal);
        }

        if (targetType == typeof(DateOnly))
        {
            return value switch
            {
                DateTime dateTime => DateOnly.FromDateTime(dateTime),
                string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
                _ => throw new InvalidCastException(
                    "Column value cannot be converted to DateOnly."
                ),
            };
        }

        if (targetType == typeof(TimeOnly))
        {
            return value switch
            {
                TimeSpan timeSpan => TimeOnly.FromTimeSpan(timeSpan),
                string text => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
                _ => throw new InvalidCastException(
                    "Column value cannot be converted to TimeOnly."
                ),
            };
        }

        if (targetType == typeof(DateTime))
        {
            return GetDateTime(ordinal);
        }

        if (targetType == typeof(Guid))
        {
            return GetGuid(ordinal);
        }

        if (targetType == typeof(bool))
        {
            return GetBoolean(ordinal);
        }

        if (targetType == typeof(byte[]) && value is byte[] bytes)
        {
            return bytes;
        }

        throw new InvalidCastException($"Column value cannot be converted to {targetType.Name}.");
    }

    private object GetNonNullValue(int ordinal)
    {
        object value = GetValue(ordinal);
        if (value == DBNull.Value)
        {
            throw new InvalidCastException("Column value is NULL.");
        }

        return value;
    }

    private long RecordsAffectedCore =>
        _bufferedResult?.RecordsAffected ?? _streamingResult?.RecordsAffected ?? -1;

    private void ReportConnectionCompletion(bool reusable)
    {
        if (_connectionCompletionReported)
        {
            return;
        }

        _connectionCompletionReported = true;
        _connection?.CompleteActiveReader(this, reusable);
    }

    private void ValidateReadableRow()
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }

        if (_currentRow is null)
        {
            throw new InvalidOperationException("The reader is not positioned on a row.");
        }
    }

    [SuppressMessage(
        "Usage",
        "CA2201:Do not raise reserved exception types",
        Justification = "DbDataReader ordinal access conventionally reports out-of-range ordinals with IndexOutOfRangeException."
    )]
    private void ValidateOrdinal(int ordinal)
    {
        if (ordinal < 0 || ordinal >= FieldCount)
        {
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is out of range.");
        }
    }
}
