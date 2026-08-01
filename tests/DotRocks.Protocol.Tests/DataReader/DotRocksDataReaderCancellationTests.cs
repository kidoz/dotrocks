using System.Data;
using System.Data.Common;
using System.Diagnostics;
using DotRocks.Data;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.DataReader;

/// <summary>
/// Row iteration must stay bounded. The command's cancellation scope used to end when the reader
/// was handed back, so a server that stopped sending mid-result left <c>Read</c>/<c>ReadAsync</c>
/// waiting forever, <c>CommandTimeout</c> inert, and <c>Cancel()</c> a no-op. These tests pin the
/// scope's extended lifetime against a fake server that deliberately stalls mid-result-set.
/// </summary>
public sealed class DotRocksDataReaderCancellationTests
{
    // Generous upper bound: the assertion is "it completed rather than hung", not a latency
    // measurement, so it stays well clear of scheduling noise on a loaded CI machine.
    private static readonly TimeSpan DidNotHangBound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ReadAsync_WhenServerStallsMidResultSet_FailsWithCommandTimeout()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            await FakeStarRocksServer
                .ReadCommandAndStallMidResultSetAsync(stream, release.Task, "1")
                .ConfigureAwait(true);
        });

        try
        {
            using var connection = new DotRocksConnection(server.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(true);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM stalled";
            command.CommandTimeout = 1;

            using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(true);
            Assert.True(await reader.ReadAsync(ct).ConfigureAwait(true));

            var stopwatch = Stopwatch.StartNew();
            DotRocksException exception = await Assert
                .ThrowsAsync<DotRocksException>(() => reader.ReadAsync(ct))
                .ConfigureAwait(true);
            stopwatch.Stop();

            Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                stopwatch.Elapsed < DidNotHangBound,
                $"The stalled read took {stopwatch.Elapsed} instead of failing on the command timeout."
            );
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public void Read_WhenServerStallsMidResultSet_FailsWithCommandTimeout()
    {
        var release = new TaskCompletionSource();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            await FakeStarRocksServer
                .ReadCommandAndStallMidResultSetAsync(stream, release.Task, "1")
                .ConfigureAwait(true);
        });

        try
        {
            using var connection = new DotRocksConnection(server.ConnectionString);
            connection.Open();
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM stalled";
            command.CommandTimeout = 1;

            using DbDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());

            // The synchronous row read carries no cancellation token; the timeout reaches it by
            // aborting the connection, which fails the in-flight socket read.
            var stopwatch = Stopwatch.StartNew();
            DotRocksException exception = Assert.Throws<DotRocksException>(() => reader.Read());
            stopwatch.Stop();

            Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                stopwatch.Elapsed < DidNotHangBound,
                $"The stalled synchronous read took {stopwatch.Elapsed} instead of failing on the command timeout."
            );
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Cancel_DuringRowIteration_CancelsTheStalledRead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            await FakeStarRocksServer
                .ReadCommandAndStallMidResultSetAsync(stream, release.Task, "1")
                .ConfigureAwait(true);
        });

        try
        {
            using var connection = new DotRocksConnection(server.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(true);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM stalled";
            // No command timeout: this pins Cancel() alone, which previously could not reach a
            // reader because the operation gate was cleared when the reader was handed back.
            command.CommandTimeout = 0;

            using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(true);
            Assert.True(await reader.ReadAsync(ct).ConfigureAwait(true));

            Task<bool> stalledRead = reader.ReadAsync(ct);
            command.Cancel();

            await Assert
                .ThrowsAnyAsync<OperationCanceledException>(() => stalledRead)
                .ConfigureAwait(true);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenServerStallsMidResultSet_DoesNotDrainIndefinitely()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource();
        using var server = FakeStarRocksServer.Start(async stream =>
        {
            await FakeStarRocksServer.CompleteAuthenticationAsync(stream).ConfigureAwait(true);
            await FakeStarRocksServer
                .ReadCommandAndStallMidResultSetAsync(stream, release.Task, "1", "2")
                .ConfigureAwait(true);
        });

        try
        {
            using var connection = new DotRocksConnection(server.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(true);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM stalled";
            command.CommandTimeout = 1;

            DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(true);
            Assert.True(await reader.ReadAsync(ct).ConfigureAwait(true));

            // Abandoning a reader whose result set never terminates must not block on draining
            // the remainder; the drain is bounded and the connection is retired instead.
            var stopwatch = Stopwatch.StartNew();
            await reader.DisposeAsync().ConfigureAwait(true);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < DidNotHangBound,
                $"Disposing the reader over a stalled result set took {stopwatch.Elapsed}."
            );
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
