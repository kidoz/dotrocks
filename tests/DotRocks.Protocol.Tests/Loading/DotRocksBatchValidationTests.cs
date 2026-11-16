using System.Data;
using System.Data.Common;
using DotRocks.Data;
using Xunit;

namespace DotRocks.Protocol.Tests.Loading;

public sealed class DotRocksBatchValidationTests
{
    [Fact]
    public void TransactionSetter_RejectsForeignProviderImmediately()
    {
        using DbBatch batch = new DotRocksBatch();
        using var transaction = new ForeignTransaction();
        Assert.Throws<InvalidOperationException>(() => batch.Transaction = transaction);
        Assert.Null(batch.Transaction);
    }

    private sealed class ForeignTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
        protected override DbConnection? DbConnection => null;

        public override void Commit() => throw new NotSupportedException();

        public override void Rollback() => throw new NotSupportedException();
    }
}
