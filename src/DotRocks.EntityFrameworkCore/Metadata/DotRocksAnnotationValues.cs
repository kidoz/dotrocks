namespace DotRocks.EntityFrameworkCore.Metadata;

/// <summary>
/// Compares DotRocks table-shape annotation values. Shared by the model validator and the relational
/// annotation provider so both agree on what counts as a conflict between annotations mapped to the
/// same StarRocks table.
/// </summary>
internal static class DotRocksAnnotationValues
{
    /// <summary>
    /// Compares two annotation values, treating string collections by ordinal element equality and
    /// falling back to <see cref="object.Equals(object?, object?)"/> for everything else.
    /// </summary>
    public static bool AreEqual(object? left, object? right)
    {
        if (left is string[] leftArray && right is string[] rightArray)
        {
            return leftArray.SequenceEqual(rightArray, StringComparer.Ordinal);
        }

        if (left is IReadOnlyList<string> leftList && right is IReadOnlyList<string> rightList)
        {
            return leftList.SequenceEqual(rightList, StringComparer.Ordinal);
        }

        return Equals(left, right);
    }
}
