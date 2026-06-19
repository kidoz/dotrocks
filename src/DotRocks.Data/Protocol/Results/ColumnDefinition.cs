namespace DotRocks.Data.Protocol.Results;

internal sealed record ColumnDefinition(
    string Catalog,
    string Schema,
    string Table,
    string OriginalTable,
    string Name,
    string OriginalName,
    ushort CharacterSet,
    uint ColumnLength,
    byte ColumnType,
    ushort Flags,
    byte Decimals
);
