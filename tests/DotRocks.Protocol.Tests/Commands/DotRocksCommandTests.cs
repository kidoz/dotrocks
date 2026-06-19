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

    [Fact]
    public void CommandTimeout_RejectsNegativeValues()
    {
        using var command = new DotRocksCommand();

        Assert.Throws<ArgumentOutOfRangeException>(() => command.CommandTimeout = -1);
    }

    [Fact]
    public void CommandTimeout_AllowsZeroForInfiniteTimeout()
    {
        using var command = new DotRocksCommand { CommandTimeout = 0 };

        Assert.Equal(0, command.CommandTimeout);
    }

    [Fact]
    public async Task ExecuteScalarAsync_WithPreCanceledToken_ThrowsOperationCanceled()
    {
        using var command = new DotRocksCommand();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert
            .ThrowsAsync<OperationCanceledException>(async () =>
                await command.ExecuteScalarAsync(cancellation.Token).ConfigureAwait(true)
            )
            .ConfigureAwait(true);
    }

    [Fact]
    public void Cancel_WithoutActiveCommand_DoesNotThrow()
    {
        using var command = new DotRocksCommand();

        command.Cancel();
    }
}
