using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DotRocks.Data;

[SuppressMessage(
    "Usage",
    "CA2201:Do not raise reserved exception types",
    Justification = "DbParameterCollection name lookups conventionally report missing parameters with IndexOutOfRangeException."
)]
internal sealed class DotRocksParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;

    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    public override int Add(object value)
    {
        _items.Add(ToParameter(value));
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (object? value in values)
        {
            Add(value ?? throw new ArgumentNullException(nameof(values)));
        }
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) =>
        value is DbParameter parameter && _items.Contains(parameter);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) =>
        ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) =>
        value is DbParameter parameter ? _items.IndexOf(parameter) : -1;

    public override int IndexOf(string parameterName)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(_items[i].ParameterName, parameterName))
            {
                return i;
            }
        }

        return -1;
    }

    public override void Insert(int index, object value) =>
        _items.Insert(index, ToParameter(value));

    public override void Remove(object value)
    {
        if (value is DbParameter parameter)
        {
            _items.Remove(parameter);
        }
    }

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        int index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new IndexOutOfRangeException($"Parameter '{parameterName}' was not found.");
        }

        RemoveAt(index);
    }

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName)
    {
        int index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new IndexOutOfRangeException($"Parameter '{parameterName}' was not found.");
        }

        return _items[index];
    }

    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        int index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new IndexOutOfRangeException($"Parameter '{parameterName}' was not found.");
        }

        _items[index] = value;
    }

    private static DbParameter ToParameter(object value) =>
        value as DbParameter
        ?? throw new ArgumentException("Value must be a DbParameter.", nameof(value));
}
