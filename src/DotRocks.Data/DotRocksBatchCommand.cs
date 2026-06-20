using System.Data;
using System.Data.Common;

namespace DotRocks.Data;

/// <summary>
/// Represents a single command within a <see cref="DotRocksBatch"/>.
/// </summary>
public sealed class DotRocksBatchCommand : DbBatchCommand
{
    private readonly DotRocksParameterCollection _parameters = new();
    private string _commandText = string.Empty;
    private int _recordsAffected = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksBatchCommand"/> class.
    /// </summary>
    public DotRocksBatchCommand() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotRocksBatchCommand"/> class.
    /// </summary>
    /// <param name="commandText">The SQL command text.</param>
    public DotRocksBatchCommand(string commandText)
    {
        _commandText = commandText ?? string.Empty;
    }

    /// <inheritdoc />
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override CommandType CommandType { get; set; } = CommandType.Text;

    /// <inheritdoc />
    public override int RecordsAffected => _recordsAffected;

    /// <inheritdoc />
    public override bool CanCreateParameter => true;

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <inheritdoc />
    public override DbParameter CreateParameter() => new DotRocksParameter();

    internal DotRocksParameterCollection DotRocksParameters => _parameters;

    internal void SetRecordsAffected(long recordsAffected) =>
        _recordsAffected = recordsAffected > int.MaxValue ? int.MaxValue : (int)recordsAffected;
}
