using System.Collections;
using System.Data.Common;

namespace DotRocks.Data;

internal sealed class DotRocksBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<DbBatchCommand> _items = [];

    public override int Count => _items.Count;

    public override bool IsReadOnly => false;

    public override void Add(DbBatchCommand item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(DbBatchCommand item) => _items.Contains(item);

    public override void CopyTo(DbBatchCommand[] array, int arrayIndex) =>
        _items.CopyTo(array, arrayIndex);

    public override IEnumerator<DbBatchCommand> GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(DbBatchCommand item) => _items.IndexOf(item);

    public override void Insert(int index, DbBatchCommand item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Insert(index, item);
    }

    public override bool Remove(DbBatchCommand item) => _items.Remove(item);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    protected override DbBatchCommand GetBatchCommand(int index) => _items[index];

    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand)
    {
        ArgumentNullException.ThrowIfNull(batchCommand);
        _items[index] = batchCommand;
    }
}
