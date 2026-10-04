using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using DataDbType = System.Data.DbType;

namespace DotRocks.EntityFrameworkCore.Storage;

/// <summary>
/// Maps a <see cref="byte"/> property to StarRocks <c>SMALLINT</c>.
/// </summary>
/// <remarks>
/// StarRocks has no unsigned integer types. Signed <c>TINYINT</c> cannot store 0–255, so the value
/// is written and read as a signed 16-bit integer. The driver then encodes it without the MySQL
/// unsigned-type flag.
/// </remarks>
internal sealed class DotRocksByteTypeMapping : RelationalTypeMapping
{
    private static readonly ValueConverter<byte, short> ByteConverter = new(
        value => (short)value,
        value => checked((byte)value)
    );

    public DotRocksByteTypeMapping()
        : this(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(typeof(byte), converter: ByteConverter),
                "smallint",
                StoreTypePostfix.None,
                DataDbType.Int16
            )
        ) { }

    private DotRocksByteTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters) { }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new DotRocksByteTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value) =>
        value switch
        {
            byte unsigned => unsigned.ToString(CultureInfo.InvariantCulture),
            short signed => signed.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Cannot generate a byte literal for value type '{value.GetType().FullName}'."
            ),
        };
}
