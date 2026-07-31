using System.Collections;
using System.Data.Common;

namespace DotRocks.FlightSql;

internal sealed class FlightSqlParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _parameters = [];

    public override int Count => _parameters.Count;

    public override object SyncRoot => ((ICollection)_parameters).SyncRoot;

    public override int Add(object value)
    {
        _parameters.Add(GetParameterValue(value));
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (object? value in values)
        {
            Add(value ?? throw new ArgumentNullException(nameof(values)));
        }
    }

    public override void Clear() => _parameters.Clear();

    public override bool Contains(object value) =>
        value is DbParameter parameter && _parameters.Contains(parameter);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) =>
        ((ICollection)_parameters).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    public override int IndexOf(object value) =>
        value is DbParameter parameter ? _parameters.IndexOf(parameter) : -1;

    public override int IndexOf(string parameterName) =>
        _parameters.FindIndex(parameter =>
            string.Equals(
                parameter.ParameterName,
                parameterName,
                StringComparison.OrdinalIgnoreCase
            )
        );

    public override void Insert(int index, object value) =>
        _parameters.Insert(index, GetParameterValue(value));

    public override void Remove(object value)
    {
        if (value is DbParameter parameter)
        {
            _parameters.Remove(parameter);
        }
    }

    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        int index = GetRequiredIndex(parameterName);
        _parameters.RemoveAt(index);
    }

    protected override DbParameter GetParameter(int index) => _parameters[index];

    protected override DbParameter GetParameter(string parameterName) =>
        _parameters[GetRequiredIndex(parameterName)];

    protected override void SetParameter(int index, DbParameter value) =>
        _parameters[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value) =>
        _parameters[GetRequiredIndex(parameterName)] = value;

    private static DbParameter GetParameterValue(object value) =>
        value as DbParameter
        ?? throw new ArgumentException("Value must be a DbParameter.", nameof(value));

    private int GetRequiredIndex(string parameterName)
    {
        int index = IndexOf(parameterName);
        return index >= 0
            ? index
            : throw new ArgumentOutOfRangeException(
                nameof(parameterName),
                $"Parameter '{parameterName}' was not found."
            );
    }
}
