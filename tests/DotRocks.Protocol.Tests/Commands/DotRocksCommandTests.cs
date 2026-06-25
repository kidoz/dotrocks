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
    public void Prepare_ValidatesTextCommandParameterShape()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @value" };
        command.Parameters.Add(new DotRocksParameter { ParameterName = "value", Value = 1 });

        command.Prepare();
    }

    [Fact]
    public void Prepare_RejectsMissingParameter()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT @missing" };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            command.Prepare
        );

        Assert.Contains("@missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsync_WithPreCanceledToken_ThrowsOperationCanceled()
    {
        using var command = new DotRocksCommand { CommandText = "SELECT 1" };
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert
            .ThrowsAsync<OperationCanceledException>(async () =>
                await command.PrepareAsync(cancellation.Token).ConfigureAwait(true)
            )
            .ConfigureAwait(true);
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

    [Fact]
    public void ParameterMode_DefaultsToAuto()
    {
        using var command = new DotRocksCommand();

        Assert.Equal(DotRocksParameterMode.Auto, command.ParameterMode);
    }

    [Fact]
    public void TextProtocolMode_Prepare_ValidatesClientSide()
    {
        using var command = new DotRocksCommand
        {
            CommandText = "SELECT @value",
            ParameterMode = DotRocksParameterMode.TextProtocol,
        };
        command.Parameters.Add(new DotRocksParameter { ParameterName = "value", Value = 1 });

        command.Prepare();
    }

    [Fact]
    public void ServerPreparedMode_Prepare_DoesNotThrow()
    {
        // Server-side preparation happens at execution; Prepare() is a no-op for this mode.
        using var command = new DotRocksCommand
        {
            CommandText = "SELECT ?",
            ParameterMode = DotRocksParameterMode.ServerPrepared,
        };

        command.Prepare();
    }

    [Fact]
    public async Task ServerPreparedMode_ExecuteScalarAsync_RequiresConnection()
    {
        using var command = new DotRocksCommand
        {
            CommandText = "SELECT 1",
            ParameterMode = DotRocksParameterMode.ServerPrepared,
        };

        // Without a live connection the mode cannot execute; behavior is verified end-to-end by the
        // ServerPrepared integration test against a real StarRocks server.
        await Assert
            .ThrowsAsync<InvalidOperationException>(async () =>
                await command
                    .ExecuteScalarAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
            .ConfigureAwait(true);
    }

    [Fact]
    public void UnsupportedFeatureException_IsDotRocksException() =>
        Assert.IsAssignableFrom<DotRocksException>(new DotRocksUnsupportedFeatureException());

    [Fact]
    public void Transaction_AssignmentSetsConnectionWhenMissing()
    {
        using var connection = new DotRocksConnection();
        using var transaction = new DotRocksTransaction(connection, IsolationLevel.ReadCommitted);
        using var command = new DotRocksCommand();

        command.Transaction = transaction;

        Assert.Same(connection, command.Connection);
        Assert.Same(transaction, command.Transaction);
    }

    [Fact]
    public void Transaction_AssignmentRejectsForeignConnection()
    {
        using var commandConnection = new DotRocksConnection();
        using var transactionConnection = new DotRocksConnection();
        using var transaction = new DotRocksTransaction(
            transactionConnection,
            IsolationLevel.ReadCommitted
        );
        using var command = new DotRocksCommand { Connection = commandConnection };

        Assert.Throws<InvalidOperationException>(() => command.Transaction = transaction);
    }

    [Fact]
    public void Transaction_AssignmentRejectsCompletedTransaction()
    {
        using var connection = new DotRocksConnection();
        using var transaction = new DotRocksTransaction(connection, IsolationLevel.ReadCommitted);
        transaction.MarkCompletedBecauseConnectionClosed();
        using var command = new DotRocksCommand { Connection = connection };

        Assert.Throws<InvalidOperationException>(() => command.Transaction = transaction);
    }
}
