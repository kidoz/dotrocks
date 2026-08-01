using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Apache.Arrow;
using DotRocks.Data;

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
    private readonly CancellationTokenSource _streamCancellation;
    private DbConnection? _connectionToClose;
    private IDisposable? _executionScope;
    private IAsyncEnumerator<RecordBatch>? _batches;
    private RecordBatch? _currentBatch;
    private ValueChunkCache<byte>? _byteChunks;
    private ValueChunkCache<char>? _charChunks;
    private bool? _hasRows;
    private int _rowIndex = -1;
    private bool _closed;

    internal DotRocksFlightSqlDataReader(
        DotRocksFlightSqlResult result,
        DbConnection? connectionToClose = null,
        IDisposable? executionScope = null,
        CancellationToken commandCancellationToken = default
    )
    {
        _result = result;
        _connectionToClose = connectionToClose;
        _executionScope = executionScope;
        _streamCancellation = commandCancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(commandCancellationToken)
            : new CancellationTokenSource();
        _schema =
            result.Schema
            ?? throw new InvalidOperationException(
                "The Flight SQL server omitted the result schema."
            );
    }

    /// <inheritdoc />
    public override int FieldCount => _schema.FieldsList.Count;

    /// <inheritdoc />
    /// <remarks>
    /// A reader created by a command has already fetched its first batch, so this reports the
    /// actual result even when the server does not declare a record count.
    /// </remarks>
    public override bool HasRows => _hasRows ?? _result.TotalRecords is null or > 0;

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
            .ReadRecordBatchesAsync(_streamCancellation.Token)
            .GetAsyncEnumerator(_streamCancellation.Token);

        ClearChunkCaches();
        if (_currentBatch is not null && _rowIndex + 1 < _currentBatch.Length)
        {
            _rowIndex++;
            _hasRows = true;
            return true;
        }

        _currentBatch?.Dispose();
        _currentBatch = null;
        _rowIndex = -1;
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            _streamCancellation
        );
        try
        {
            while (await _batches.MoveNextAsync().ConfigureAwait(false))
            {
                if (_batches.Current.Length == 0)
                {
                    _batches.Current.Dispose();
                    continue;
                }

                _currentBatch = _batches.Current;
                _rowIndex = 0;
                _hasRows = true;
                return true;
            }
        }
        catch
        {
            CompleteExecution();
            throw;
        }

        _hasRows ??= false;
        CompleteExecution();
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
    /// <exception cref="IndexOutOfRangeException">The column name is not in the result.</exception>
    [SuppressMessage(
        "Usage",
        "CA2201:Do not raise reserved exception types",
        Justification = "DbDataReader.GetOrdinal conventionally reports a missing column with IndexOutOfRangeException."
    )]
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

        throw new IndexOutOfRangeException($"Column '{name}' was not found.");
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
    /// <exception cref="DotRocksPrecisionLossException">
    /// The StarRocks value has more precision than <see cref="decimal" /> can represent. Read it as
    /// <see cref="DotRocksDecimal" /> instead.
    /// </exception>
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

        // StarRocks decimals wider than System.Decimal materialize as DotRocksDecimal, which is not
        // convertible through IConvertible.
        if (typeof(T) == typeof(decimal) && value is DotRocksDecimal wide)
        {
            return (T)(object)wide.ToDecimal();
        }

        if (typeof(T) == typeof(DotRocksDecimal) && value is decimal narrow)
        {
            return (T)(object)(DotRocksDecimal)narrow;
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
    /// <remarks>
    /// The materialized value is cached for the current row and column, so reading a large value in
    /// chunks does not re-materialize it for every chunk.
    /// </remarks>
    public override long GetBytes(
        int ordinal,
        long dataOffset,
        byte[]? buffer,
        int bufferOffset,
        int length
    )
    {
        EnsureCurrentRow();
        _byteChunks ??= new ValueChunkCache<byte>();
        if (!_byteChunks.TryGet(_currentBatch!, ordinal, _rowIndex, out byte[]? value))
        {
            value = GetFieldValue<byte[]>(ordinal);
            _byteChunks.Store(_currentBatch!, ordinal, _rowIndex, value);
        }

        return CopyValue(value, dataOffset, buffer, bufferOffset, length);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The materialized value is cached for the current row and column, so reading a large value in
    /// chunks does not re-materialize it for every chunk.
    /// </remarks>
    public override long GetChars(
        int ordinal,
        long dataOffset,
        char[]? buffer,
        int bufferOffset,
        int length
    )
    {
        EnsureCurrentRow();
        _charChunks ??= new ValueChunkCache<char>();
        if (!_charChunks.TryGet(_currentBatch!, ordinal, _rowIndex, out char[]? value))
        {
            value = GetString(ordinal).ToCharArray();
            _charChunks.Store(_currentBatch!, ordinal, _rowIndex, value);
        }

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
            ClearChunkCaches();
            _currentBatch?.Dispose();
            _currentBatch = null;
            if (_batches is not null)
            {
                await _batches.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _streamCancellation.Dispose();
            CompleteExecution();
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

    /// <summary>
    /// Creates a reader whose first batch has already been fetched, so <see cref="HasRows" />
    /// reports the real result before the first <see cref="ReadAsync" />.
    /// </summary>
    /// <remarks>
    /// The command scope and the connection are handed over only after the first batch arrives, so
    /// a failed fetch leaves both for the caller to release or retry on another transport.
    /// </remarks>
    internal static async Task<DotRocksFlightSqlDataReader> CreateAsync(
        DotRocksFlightSqlResult result,
        DbConnection? connectionToClose,
        IDisposable? executionScope,
        CancellationToken commandCancellationToken
    )
    {
        var reader = new DotRocksFlightSqlDataReader(
            result,
            commandCancellationToken: commandCancellationToken
        );
        try
        {
            await reader.PrimeAsync().ConfigureAwait(false);
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        reader._connectionToClose = connectionToClose;
        reader._executionScope = executionScope;
        return reader;
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

    private async Task PrimeAsync()
    {
        _batches ??= _result
            .ReadRecordBatchesAsync(_streamCancellation.Token)
            .GetAsyncEnumerator(_streamCancellation.Token);

        while (await _batches.MoveNextAsync().ConfigureAwait(false))
        {
            if (_batches.Current.Length == 0)
            {
                _batches.Current.Dispose();
                continue;
            }

            _currentBatch = _batches.Current;
            _rowIndex = -1;
            _hasRows = true;
            return;
        }

        _hasRows = false;
    }

    [SuppressMessage(
        "Usage",
        "CA2201:Do not raise reserved exception types",
        Justification = "DbDataReader ordinal access conventionally reports out-of-range ordinals with IndexOutOfRangeException."
    )]
    private Field GetField(int ordinal)
    {
        if (ordinal < 0 || ordinal >= FieldCount)
        {
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is out of range.");
        }

        return _schema.GetFieldByIndex(ordinal);
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

    private void ClearChunkCaches()
    {
        _byteChunks?.Clear();
        _charChunks?.Clear();
    }

    private void CompleteExecution() => Interlocked.Exchange(ref _executionScope, null)?.Dispose();

    /// <summary>
    /// Holds the value materialized for one row and column so that chunked reads reuse it.
    /// </summary>
    private sealed class ValueChunkCache<T>
    {
        private RecordBatch? _batch;
        private T[]? _value;
        private int _ordinal = -1;
        private int _rowIndex = -1;

        public bool TryGet(
            RecordBatch batch,
            int ordinal,
            int rowIndex,
            [NotNullWhen(true)] out T[]? value
        )
        {
            if (
                _value is not null
                && ReferenceEquals(_batch, batch)
                && _ordinal == ordinal
                && _rowIndex == rowIndex
            )
            {
                value = _value;
                return true;
            }

            value = null;
            return false;
        }

        public void Store(RecordBatch batch, int ordinal, int rowIndex, T[] value)
        {
            _batch = batch;
            _ordinal = ordinal;
            _rowIndex = rowIndex;
            _value = value;
        }

        public void Clear()
        {
            _batch = null;
            _value = null;
            _ordinal = -1;
            _rowIndex = -1;
        }
    }
}
