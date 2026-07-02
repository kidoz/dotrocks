namespace DotRocks.EntityFrameworkCore.Metadata;

/// <summary>
/// Compares DotRocks table-shape annotation values. Shared by the model validator and the relational
/// annotation provider so both agree on what counts as a conflict between annotations mapped to the
/// same StarRocks table.
/// </summary>
internal static class DotRocksAnnotationValues
{
    /// <summary>
    /// Compares two annotation values, treating string collections by ordinal element equality,
    /// string dictionaries by ordinal entry equality, and falling back to
    /// <see cref="object.Equals(object?, object?)"/> for everything else.
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

        if (
            left is IReadOnlyDictionary<string, string> leftMap
            && right is IReadOnlyDictionary<string, string> rightMap
        )
        {
            return leftMap.Count == rightMap.Count
                && leftMap.All(entry =>
                    rightMap.TryGetValue(entry.Key, out string? value)
                    && string.Equals(entry.Value, value, StringComparison.Ordinal)
                );
        }

        return Equals(left, right);
    }
}
