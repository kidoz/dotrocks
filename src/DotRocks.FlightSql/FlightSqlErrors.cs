using Grpc.Core;

namespace DotRocks.FlightSql;

internal static class FlightSqlErrors
{
    public static bool IsRemoteFailure(Exception exception) =>
        FindRemoteFailure(exception) is not null;

    internal static bool IsUnauthenticated(Exception exception) =>
        FindRemoteFailure(exception) is RpcException { StatusCode: StatusCode.Unauthenticated };

    private static Exception? FindRemoteFailure(Exception exception)
    {
        // Arrow's SQL client wraps discovery RPC errors in InvalidOperationException. Drop that
        // wrapper too: merely replacing the outer message still exposes its inner status details.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is RpcException or HttpRequestException or OperationCanceledException)
            {
                return current;
            }
        }

        return null;
    }

    // Neither status details, trailers nor inner exceptions are safe: servers can echo SQL,
    // bound parameters or credentials in any of them. Preserve only machine-readable status.
    public static Exception Sanitize(Exception exception) =>
        FindRemoteFailure(exception) switch
        {
            RpcException rpc => SanitizeRpc(rpc),
            HttpRequestException http => new HttpRequestException(
                "The Flight SQL HTTP transport failed.",
                null,
                http.StatusCode
            ),
            OperationCanceledException canceled => new OperationCanceledException(
                "The Flight SQL operation was canceled.",
                canceled.CancellationToken
            ),
            _ => throw new ArgumentException(
                "The exception is not a remote failure.",
                nameof(exception)
            ),
        };

    private static RpcException SanitizeRpc(RpcException original)
    {
        var sanitized = new RpcException(
            new Status(original.StatusCode, "The Flight SQL operation failed.")
        );
        // Explicit inspection only: Exception.ToString never includes Data. Bound the detail and
        // remove terminal controls, but treat the remainder as sensitive server-supplied text.
        string detail = original.Data["DotRocks.ServerMessage"] as string ?? original.Status.Detail;
        sanitized.Data["DotRocks.ServerMessage"] = new string(
            detail.Take(4096).Where(character => !char.IsControl(character)).ToArray()
        );
        return sanitized;
    }

    public static async Task<bool> ReadNextAsync<T>(
        IAsyncStreamReader<T> reader,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await reader.MoveNext(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRemoteFailure(exception))
        {
            throw Sanitize(exception);
        }
    }
}
