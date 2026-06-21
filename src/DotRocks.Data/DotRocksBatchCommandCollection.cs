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
        _items.Add(Validate(item));
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(DbBatchCommand item) => _items.Contains(item);

    public override void CopyTo(DbBatchCommand[] array, int arrayIndex) =>
        _items.CopyTo(array, arrayIndex);

    public override IEnumerator<DbBatchCommand> GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(DbBatchCommand item) => _items.IndexOf(item);

    public override void Insert(int index, DbBatchCommand item)
    {
        _items.Insert(index, Validate(item));
    }

    public override bool Remove(DbBatchCommand item) => _items.Remove(item);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    protected override DbBatchCommand GetBatchCommand(int index) => _items[index];

    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand)
    {
        _items[index] = Validate(batchCommand);
    }

    // Only DotRocksBatchCommand objects (created by DotRocksBatch.CreateBatchCommand) can execute,
    // because execution casts to that type. Reject foreign commands at insertion with a clear
    // message instead of letting them fail later with an InvalidCastException.
    private static DotRocksBatchCommand Validate(DbBatchCommand item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item as DotRocksBatchCommand
            ?? throw new ArgumentException(
                "DotRocksBatch only accepts batch commands created by DotRocksBatch.CreateBatchCommand().",
                nameof(item)
            );
    }
}
