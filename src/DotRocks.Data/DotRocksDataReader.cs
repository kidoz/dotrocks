using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotRocks.Data.Protocol.Results;

namespace DotRocks.Data;

/// <summary>
/// Reads rows returned by a DotRocks command.
/// </summary>
public sealed class DotRocksDataReader : DbDataReader, IEnumerable<IDataRecord>
{
    private readonly QueryResult _result;
    private readonly DotRocksConnection? _connection;
    private readonly CommandBehavior _behavior;
    private int _rowIndex = -1;
    private bool _isClosed;

    internal DotRocksDataReader(
        QueryResult result,
        DotRocksConnection? connection = null,
        CommandBehavior behavior = CommandBehavior.Default
    )
    {
        _result = result;
        _connection = connection;
        _behavior = behavior;
    }

    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc />
    public override int Depth => 0;

    /// <inheritdoc />
    public override int FieldCount => _result.Columns.Count;

    /// <inheritdoc />
    public override bool HasRows => _result.Rows.Count > 0;

    /// <inheritdoc />
    public override bool IsClosed => _isClosed;

    /// <inheritdoc />
    public override int RecordsAffected =>
        _result.RecordsAffected > int.MaxValue ? int.MaxValue : (int)_result.RecordsAffected;

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
        return ColumnTypeMapper.GetDataTypeName(_result.Columns[ordinal].ColumnType);
    }

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal) =>
        Convert.ToDateTime(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal) =>
        Convert.ToDecimal(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override double GetDouble(int ordinal) =>
        Convert.ToDouble(GetNonNullValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override Type GetFieldType(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return ColumnTypeMapper.GetFieldType(_result.Columns[ordinal].ColumnType);
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
        return _result.Columns[ordinal].Name;
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
        for (int i = 0; i < _result.Columns.Count; i++)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(_result.Columns[i].Name, name))
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
        return _result.Rows[_rowIndex][ordinal] ?? DBNull.Value;
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
    public override bool NextResult() => false;

    /// <inheritdoc />
    public override bool Read()
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }

        if (_rowIndex + 1 >= _result.Rows.Count)
        {
            return false;
        }

        _rowIndex++;
        return true;
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
        _isClosed = true;
        if ((_behavior & CommandBehavior.CloseConnection) != 0)
        {
            _connection?.Close();
        }
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

    private void ValidateReadableRow()
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }

        if (_rowIndex < 0 || _rowIndex >= _result.Rows.Count)
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
