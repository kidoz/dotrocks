using System.Data;
using DotRocks.Data;
using Xunit;

namespace DotRocks.Protocol.Tests.Commands;

public sealed class DotRocksCommandTests
{
    [Fact]
    public void CommandType_RejectsNonTextCommands()
    {
        using var command = new DotRocksCommand();

        Assert.Throws<NotSupportedException>(() =>
            command.CommandType = CommandType.StoredProcedure
        );
    }

    [Fact]
    public void CreateParameter_ReturnsDotRocksParameter()
    {
        using var command = new DotRocksCommand();

        Assert.IsType<DotRocksParameter>(command.CreateParameter());
    }
}
