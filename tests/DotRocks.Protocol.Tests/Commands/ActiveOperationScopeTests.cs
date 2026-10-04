using DotRocks.Data;
using Xunit;

namespace DotRocks.Protocol.Tests.Commands;

public sealed class ActiveOperationScopeTests
{
    [Fact]
    public void Cancel_AfterScopeDisposed_DoesNotThrow()
    {
        var gate = new ActiveOperationGate();
        var scope = new ActiveOperationScope(
            gate,
            timeoutSeconds: 0,
            "conflict",
            CancellationToken.None
        );
        scope.Dispose();

        Assert.False(gate.TryCancelActiveOperation());
    }

    [Fact]
    public void TimeoutReschedule_AfterScopeDisposed_DoesNotThrow()
    {
        var gate = new ActiveOperationGate();
        var scope = new ActiveOperationScope(
            gate,
            timeoutSeconds: 30,
            "conflict",
            CancellationToken.None
        );
        scope.Dispose();

        scope.ResumeTimeout();
        scope.SuspendTimeout();
    }

    [Fact]
    public async Task Cancel_RacesScopeDisposal_DoesNotThrow()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            var gate = new ActiveOperationGate();
            var scope = new ActiveOperationScope(
                gate,
                timeoutSeconds: 0,
                "conflict",
                CancellationToken.None
            );
            using var started = new Barrier(2);
            try
            {
                Task cancel = Task.Run(
                    () =>
                    {
                        started.SignalAndWait(cancellationToken);
                        for (int attempt = 0; attempt < 10; attempt++)
                        {
                            gate.TryCancelActiveOperation();
                        }
                    },
                    cancellationToken
                );
                Task dispose = Task.Run(
                    () =>
                    {
                        started.SignalAndWait(cancellationToken);
                        scope.Dispose();
                    },
                    cancellationToken
                );
                await Task.WhenAll(cancel, dispose)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(true);
            }
            finally
            {
                scope.Dispose();
            }
        }
    }

    [Fact]
    public async Task TimeoutReschedule_RacesScopeDisposal_DoesNotThrow()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var gate = new ActiveOperationGate();
            var scope = new ActiveOperationScope(
                gate,
                timeoutSeconds: 30,
                "conflict",
                CancellationToken.None
            );
            using var started = new Barrier(2);
            try
            {
                Task reschedule = Task.Run(
                    () =>
                    {
                        started.SignalAndWait(cancellationToken);
                        for (int attempt = 0; attempt < 20; attempt++)
                        {
                            scope.ResumeTimeout();
                            scope.SuspendTimeout();
                        }
                    },
                    cancellationToken
                );
                Task dispose = Task.Run(
                    () =>
                    {
                        started.SignalAndWait(cancellationToken);
                        scope.Dispose();
                    },
                    cancellationToken
                );
                await Task.WhenAll(reschedule, dispose)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(true);
            }
            finally
            {
                scope.Dispose();
            }
        }
    }
}
