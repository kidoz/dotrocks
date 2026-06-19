using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DotRocks.Data;

/// <summary>
/// Represents a DotRocks command parameter.
/// </summary>
public sealed class DotRocksParameter : DbParameter
{
    /// <inheritdoc />
    public override DbType DbType { get; set; }

    /// <inheritdoc />
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    /// <inheritdoc />
    public override bool IsNullable { get; set; }

    /// <inheritdoc />
    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    /// <inheritdoc />
    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object? Value { get; set; }

    /// <inheritdoc />
    public override bool SourceColumnNullMapping { get; set; }

    /// <inheritdoc />
    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

    /// <inheritdoc />
    public override int Size { get; set; }

    /// <inheritdoc />
    public override void ResetDbType() => DbType = DbType.String;
}
