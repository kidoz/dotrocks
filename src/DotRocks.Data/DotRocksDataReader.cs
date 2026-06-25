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
    private readonly IReadOnlyList<QueryResult>? _bufferedResults;
    private readonly StreamingQueryResult? _streamingResult;
    private readonly TextResultRowReader? _rowReader;
    private IReadOnlyList<ColumnDefinition> _columns;
    private readonly DotRocksConnection? _connection;
    private readonly CommandBehavior _behavior;
    private ReadOnlyCollection<DbColumn>? _columnSchema;
    private object?[]? _currentRow;
    private object?[]? _prefetchedRow;
    private int _rowIndex = -1;
    private int _bufferedIndex;
    private bool _isClosed;
    private bool _isConsumed;
    private bool _connectionCompletionReported;
    private bool _hasPrefetchedRow;

    internal DotRocksDataReader(
        QueryResult result,
        DotRocksConnection? connection = null,
        CommandBehavior behavior = CommandBehavior.Default
    )
    {
        _bufferedResults = [result];
        _columns = result.Columns;
        _connection = connection;
        _behavior = behavior;
    }

    internal DotRocksDataReader(
        IReadOnlyList<QueryResult> results,
        DotRocksConnection? connection,
        CommandBehavior behavior
    )
    {
        ArgumentNullException.ThrowIfNull(results);
        _bufferedResults = results;
        _columns = results.Count > 0 ? results[0].Columns : [];
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

    private QueryResult? CurrentBufferedResult =>
        _bufferedResults is not null && _bufferedIndex < _bufferedResults.Count
            ? _bufferedResults[_bufferedIndex]
            : null;

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
        _bufferedResults is not null ? CurrentBufferedResult?.Rows.Count > 0 : HasStreamingRows();

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
    )
    {
        byte[] bytes = GetFieldValue<byte[]>(ordinal);
        if (buffer is null)
        {
            return bytes.Length;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bufferOffset, buffer.Length);

        if (dataOffset >= bytes.Length || length == 0 || bufferOffset == buffer.Length)
        {
            return 0;
        }

        int sourceOffset =
            dataOffset > int.MaxValue
                ? throw new ArgumentOutOfRangeException(nameof(dataOffset))
                : (int)dataOffset;
        int count = Math.Min(length, bytes.Length - sourceOffset);
        count = Math.Min(count, buffer.Length - bufferOffset);
        Array.Copy(bytes, sourceOffset, buffer, bufferOffset, count);
        return count;
    }

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
    )
    {
        string text = GetString(ordinal);
        if (buffer is null)
        {
            return text.Length;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bufferOffset, buffer.Length);

        if (dataOffset >= text.Length || length == 0 || bufferOffset == buffer.Length)
        {
            return 0;
        }

        int sourceOffset =
            dataOffset > int.MaxValue
                ? throw new ArgumentOutOfRangeException(nameof(dataOffset))
                : (int)dataOffset;
        int count = Math.Min(length, text.Length - sourceOffset);
        count = Math.Min(count, buffer.Length - bufferOffset);
        text.CopyTo(sourceOffset, buffer, bufferOffset, count);
        return count;
    }

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
        ColumnDefinition column = _columns[ordinal];
        return ColumnTypeMapper.GetFieldType(column.ColumnType, column.ColumnLength);
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
    public override Guid GetGuid(int ordinal)
    {
        object value = GetNonNullValue(ordinal);
        return value switch
        {
            Guid guid => guid,
            byte[] { Length: 16 } bytes => new Guid(bytes),
            string text => Guid.Parse(text),
            _ => Guid.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)
                    ?? throw new InvalidCastException("Column value cannot be converted to Guid.")
            ),
        };
    }

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
    public override bool NextResult()
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }

        // Only buffered batches expose multiple result sets; the streaming single-command path
        // has exactly one. Advancing resets row positioning and re-points the column metadata.
        if (_bufferedResults is null || _bufferedIndex + 1 >= _bufferedResults.Count)
        {
            return false;
        }

        _bufferedIndex++;
        _columns = _bufferedResults[_bufferedIndex].Columns;
        _columnSchema = null;
        _currentRow = null;
        _rowIndex = -1;
        _isConsumed = false;
        return true;
    }

    /// <inheritdoc />
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextResult());
    }

    /// <inheritdoc />
    public override DataTable GetSchemaTable()
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }

        var schemaTable = new DataTable("SchemaTable") { Locale = CultureInfo.InvariantCulture };
        schemaTable.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        schemaTable.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        schemaTable.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        schemaTable.Columns.Add("DataTypeName", typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.BaseColumnName, typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.BaseTableName, typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.BaseSchemaName, typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.IsAliased, typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.IsExpression, typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));

        foreach (DbColumn column in GetColumnSchema())
        {
            DataRow row = schemaTable.NewRow();
            row[SchemaTableColumn.ColumnName] = column.ColumnName;
            row[SchemaTableColumn.ColumnOrdinal] = column.ColumnOrdinal ?? 0;
            row[SchemaTableColumn.ColumnSize] = column.ColumnSize ?? -1;
            row[SchemaTableColumn.DataType] = column.DataType ?? typeof(object);
            row["DataTypeName"] = column.DataTypeName ?? string.Empty;
            row[SchemaTableColumn.AllowDBNull] = column.AllowDBNull ?? true;
            row[SchemaTableColumn.BaseColumnName] = (object?)column.BaseColumnName ?? DBNull.Value;
            row[SchemaTableColumn.BaseTableName] = (object?)column.BaseTableName ?? DBNull.Value;
            row[SchemaTableColumn.BaseSchemaName] = (object?)column.BaseSchemaName ?? DBNull.Value;
            row[SchemaTableColumn.IsAliased] = column.IsAliased ?? false;
            row[SchemaTableColumn.IsExpression] = column.IsExpression ?? false;
            row[SchemaTableColumn.IsKey] = column.IsKey ?? false;
            row[SchemaTableColumn.IsLong] = column.IsLong ?? false;
            row[SchemaTableColumn.IsUnique] = column.IsUnique ?? false;
            schemaTable.Rows.Add(row);
        }

        return schemaTable;
    }

    /// <inheritdoc />
    public override bool Read()
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }

        if (_bufferedResults is not null)
        {
            QueryResult? current = CurrentBufferedResult;
            if (current is null || _rowIndex + 1 >= current.Rows.Count)
            {
                _isConsumed = true;
                ReportConnectionCompletion(reusable: true);
                return false;
            }

            _rowIndex++;
            _currentRow = current.Rows[_rowIndex];
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

        if (_bufferedResults is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Read();
        }

        return await ReadStreamingRowAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReadStreamingRowAsync(CancellationToken cancellationToken)
    {
        // Honor cancellation at entry like the buffered path. A cancelled streaming read leaves
        // the result set partially consumed, so the physical connection is mid-stream and must be
        // discarded rather than returned to the pool. (Mid-flight cancellation during a network
        // read is handled by FetchStreamingRowAsync's catch.)
        if (cancellationToken.IsCancellationRequested)
        {
            _isConsumed = true;
            ReportConnectionCompletion(reusable: false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (_hasPrefetchedRow)
        {
            _currentRow = _prefetchedRow;
            _prefetchedRow = null;
            _hasPrefetchedRow = false;
            _rowIndex++;
            return true;
        }

        object?[]? row = await FetchStreamingRowAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        _rowIndex++;
        _currentRow = row;
        return true;
    }

    private async Task<object?[]?> FetchStreamingRowAsync(CancellationToken cancellationToken)
    {
        if (_isConsumed)
        {
            return null;
        }

        if (_rowReader is null)
        {
            _isConsumed = true;
            ReportConnectionCompletion(reusable: true);
            return null;
        }

        try
        {
            object?[]? row = await _rowReader.ReadRowAsync(cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                _isConsumed = true;
                _currentRow = null;
                ReportConnectionCompletion(reusable: true);
                return null;
            }

            return row;
        }
        catch
        {
            _isConsumed = true;
            ReportConnectionCompletion(reusable: false);
            throw;
        }
    }

    private bool HasStreamingRows()
    {
        if (_streamingResult?.HasResultSet != true || _isConsumed)
        {
            return false;
        }

        if (_currentRow is not null || _hasPrefetchedRow)
        {
            return true;
        }

        _prefetchedRow = FetchStreamingRowAsync(CancellationToken.None).GetAwaiter().GetResult();
        _hasPrefetchedRow = _prefetchedRow is not null;
        return _hasPrefetchedRow;
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
            _isConsumed || _bufferedResults is not null || _streamingResult?.HasResultSet != true;
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
                ColumnTypeMapper.GetFieldType(definition.ColumnType, definition.ColumnLength),
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

        if (targetType == typeof(sbyte))
        {
            return Convert.ToSByte(value, CultureInfo.InvariantCulture);
        }

        if (targetType == typeof(short))
        {
            return GetInt16(ordinal);
        }

        if (targetType == typeof(int))
        {
            return GetInt32(ordinal);
        }

        if (targetType == typeof(long))
        {
            return GetInt64(ordinal);
        }

        if (targetType == typeof(Int128))
        {
            return value switch
            {
                Int128 int128 => int128,
                string text => Int128.Parse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture
                ),
                _ => (Int128)Convert.ToInt64(value, CultureInfo.InvariantCulture),
            };
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

        if (targetType == typeof(DotRocksJson))
        {
            // JSON arrives over the text protocol as a string; preserve the exact bytes losslessly.
            return value as DotRocksJson ?? new DotRocksJson(GetString(ordinal));
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

    private long RecordsAffectedCore
    {
        get
        {
            if (_bufferedResults is null)
            {
                return _streamingResult?.RecordsAffected ?? -1;
            }

            long total = -1;
            foreach (QueryResult result in _bufferedResults)
            {
                if (result.RecordsAffected >= 0)
                {
                    total = (total < 0 ? 0 : total) + result.RecordsAffected;
                }
            }

            return total;
        }
    }

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
