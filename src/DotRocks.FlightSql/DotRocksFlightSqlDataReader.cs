using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Apache.Arrow;

namespace DotRocks.FlightSql;

/// <summary>
/// Exposes a streamed Arrow Flight SQL result through the asynchronous ADO.NET reader surface.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1010:Generic interface should also be implemented",
    Justification = "DbDataReader defines the non-generic enumeration contract."
)]
public sealed class DotRocksFlightSqlDataReader : DbDataReader
{
    private readonly DotRocksFlightSqlResult _result;
    private readonly Schema _schema;
    private readonly DbConnection? _connectionToClose;
    private IAsyncEnumerator<RecordBatch>? _batches;
    private RecordBatch? _currentBatch;
    private int _rowIndex = -1;
    private bool _closed;

    internal DotRocksFlightSqlDataReader(
        DotRocksFlightSqlResult result,
        DbConnection? connectionToClose = null
    )
    {
        _result = result;
        _connectionToClose = connectionToClose;
        _schema =
            result.Schema
            ?? throw new InvalidOperationException(
                "The Flight SQL server omitted the result schema."
            );
    }

    /// <inheritdoc />
    public override int FieldCount => _schema.FieldsList.Count;

    /// <inheritdoc />
    public override bool HasRows => _result.TotalRecords is null or > 0;

    /// <inheritdoc />
    public override bool IsClosed => _closed;

    /// <inheritdoc />
    public override int RecordsAffected => -1;

    /// <inheritdoc />
    public override int Depth => 0;

    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc />
    public override bool Read() =>
        throw new NotSupportedException("Flight SQL result streaming is asynchronous only.");

    /// <inheritdoc />
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _batches ??= _result
            .ReadRecordBatchesAsync(cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        if (_currentBatch is not null && _rowIndex + 1 < _currentBatch.Length)
        {
            _rowIndex++;
            return true;
        }

        _currentBatch?.Dispose();
        _currentBatch = null;
        _rowIndex = -1;
        while (await _batches.MoveNextAsync().ConfigureAwait(false))
        {
            if (_batches.Current.Length == 0)
            {
                _batches.Current.Dispose();
                continue;
            }

            _currentBatch = _batches.Current;
            _rowIndex = 0;
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public override bool NextResult() => false;

    /// <inheritdoc />
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public override string GetName(int ordinal) => GetField(ordinal).Name;

    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal) => GetField(ordinal).DataType.Name;

    /// <inheritdoc />
    public override Type GetFieldType(int ordinal) =>
        ArrowValueConverter.GetFieldType(GetField(ordinal).DataType);

    /// <inheritdoc />
    public override object GetValue(int ordinal)
    {
        EnsureCurrentRow();
        return ArrowValueConverter.GetValue(_currentBatch!.Column(ordinal), _rowIndex);
    }

    /// <inheritdoc />
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = Math.Min(values.Length, FieldCount);
        for (int index = 0; index < count; index++)
        {
            values[index] = GetValue(index);
        }

        return count;
    }

    /// <inheritdoc />
    public override int GetOrdinal(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        for (int index = 0; index < FieldCount; index++)
        {
            if (string.Equals(GetName(index), name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), $"Column '{name}' was not found.");
    }

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    /// <inheritdoc />
    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    /// <inheritdoc />
    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal) =>
        GetValue(ordinal) switch
        {
            DateTime value => value,
            DateOnly value => value.ToDateTime(TimeOnly.MinValue),
            DateTimeOffset value => value.DateTime,
            object value => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
        };

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);

    /// <inheritdoc />
    public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);

    /// <inheritdoc />
    public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) =>
        GetValue(ordinal) switch
        {
            Guid value => value,
            byte[] value => new Guid(value),
            string value => Guid.Parse(value),
            object value => throw new InvalidCastException(
                $"A value of type '{value.GetType().Name}' cannot be converted to Guid."
            ),
        };

    /// <inheritdoc />
    public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);

    /// <inheritdoc />
    public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);

    /// <inheritdoc />
    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);

    /// <inheritdoc />
    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);

    /// <inheritdoc />
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    /// <inheritdoc />
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsDBNull(ordinal));
    }

    /// <inheritdoc />
    public override T GetFieldValue<T>(int ordinal)
    {
        object value = GetValue(ordinal);
        if (value is T typed)
        {
            return typed;
        }

        if (value is DBNull)
        {
            throw new InvalidCastException("A database null cannot be converted to a CLR value.");
        }

        if (typeof(T) == typeof(Guid) && value is byte[] bytes)
        {
            return (T)(object)new Guid(bytes);
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetFieldValue<T>(ordinal));
    }

    /// <inheritdoc />
    public override long GetBytes(
        int ordinal,
        long dataOffset,
        byte[]? buffer,
        int bufferOffset,
        int length
    )
    {
        byte[] value = GetFieldValue<byte[]>(ordinal);
        return CopyValue(value, dataOffset, buffer, bufferOffset, length);
    }

    /// <inheritdoc />
    public override long GetChars(
        int ordinal,
        long dataOffset,
        char[]? buffer,
        int bufferOffset,
        int length
    )
    {
        char[] value = GetString(ordinal).ToCharArray();
        return CopyValue(value, dataOffset, buffer, bufferOffset, length);
    }

    /// <inheritdoc />
    public override DataTable? GetSchemaTable() => null;

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            _currentBatch?.Dispose();
            _currentBatch = null;
            if (_batches is not null)
            {
                await _batches.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (_connectionToClose is not null)
            {
                await _connectionToClose.CloseAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    private static long CopyValue<T>(
        T[] value,
        long dataOffset,
        T[]? buffer,
        int bufferOffset,
        int length
    )
    {
        if (buffer is null)
        {
            return value.Length;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        int sourceOffset = checked((int)dataOffset);
        if (sourceOffset >= value.Length)
        {
            return 0;
        }

        int count = Math.Min(length, value.Length - sourceOffset);
        System.Array.Copy(value, sourceOffset, buffer, bufferOffset, count);
        return count;
    }

    private Field GetField(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        return ordinal < FieldCount
            ? _schema.GetFieldByIndex(ordinal)
            : throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                $"Column ordinal {ordinal} is out of range."
            );
    }

    private void EnsureCurrentRow()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_currentBatch is null || _rowIndex < 0)
        {
            throw new InvalidOperationException(
                "ReadAsync must be called before accessing values."
            );
        }
    }
}
