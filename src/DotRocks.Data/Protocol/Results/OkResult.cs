namespace DotRocks.Data.Protocol.Results;

internal sealed record OkResult(
    long AffectedRows,
    ulong LastInsertId,
    ushort StatusFlags,
    ushort Warnings
);
