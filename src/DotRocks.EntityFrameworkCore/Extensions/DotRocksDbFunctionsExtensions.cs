namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// DotRocks-specific extension methods on <see cref="EF.Functions"/> that translate to native
/// StarRocks SQL functions.
/// </summary>
/// <remarks>
/// <para>
/// <b>NULL semantics.</b> StarRocks follows MySQL semantics for <c>greatest()</c> and
/// <c>least()</c>: the result is <see langword="null"/> when <em>any</em> argument is
/// <see langword="null"/>. This differs from PostgreSQL, where <c>GREATEST</c>/<c>LEAST</c>
/// ignore NULL arguments. When migrating from a PostgreSQL provider, wrap nullable arguments in
/// a coalescing expression (for example <c>x.Value ?? DateTime.MinValue</c>) if rows with NULL
/// values must not be filtered out or projected as NULL.
/// </para>
/// </remarks>
public static class DotRocksDbFunctionsExtensions
{
    /// <summary>
    /// Returns the largest of two values, translated to the StarRocks <c>greatest()</c> function.
    /// Returns <see langword="null"/> when any argument is <see langword="null"/> (MySQL
    /// semantics; see the class remarks).
    /// </summary>
    /// <typeparam name="T">The compared value type.</typeparam>
    /// <param name="_">The <see cref="DbFunctions"/> instance.</param>
    /// <param name="value1">The first value.</param>
    /// <param name="value2">The second value.</param>
    /// <returns>The largest value, or <see langword="null"/> when any argument is NULL.</returns>
    public static T Greatest<T>(this DbFunctions _, T value1, T value2) =>
        throw CreateClientEvaluationException(nameof(Greatest));

    /// <summary>
    /// Returns the largest of three values, translated to the StarRocks <c>greatest()</c>
    /// function. Returns <see langword="null"/> when any argument is <see langword="null"/>
    /// (MySQL semantics; see the class remarks).
    /// </summary>
    /// <typeparam name="T">The compared value type.</typeparam>
    /// <param name="_">The <see cref="DbFunctions"/> instance.</param>
    /// <param name="value1">The first value.</param>
    /// <param name="value2">The second value.</param>
    /// <param name="value3">The third value.</param>
    /// <returns>The largest value, or <see langword="null"/> when any argument is NULL.</returns>
    public static T Greatest<T>(this DbFunctions _, T value1, T value2, T value3) =>
        throw CreateClientEvaluationException(nameof(Greatest));

    /// <summary>
    /// Returns the largest of four values, translated to the StarRocks <c>greatest()</c>
    /// function. Returns <see langword="null"/> when any argument is <see langword="null"/>
    /// (MySQL semantics; see the class remarks).
    /// </summary>
    /// <typeparam name="T">The compared value type.</typeparam>
    /// <param name="_">The <see cref="DbFunctions"/> instance.</param>
    /// <param name="value1">The first value.</param>
    /// <param name="value2">The second value.</param>
    /// <param name="value3">The third value.</param>
    /// <param name="value4">The fourth value.</param>
    /// <returns>The largest value, or <see langword="null"/> when any argument is NULL.</returns>
    public static T Greatest<T>(this DbFunctions _, T value1, T value2, T value3, T value4) =>
        throw CreateClientEvaluationException(nameof(Greatest));

    /// <summary>
    /// Returns the smallest of two values, translated to the StarRocks <c>least()</c> function.
    /// Returns <see langword="null"/> when any argument is <see langword="null"/> (MySQL
    /// semantics; see the class remarks).
    /// </summary>
    /// <typeparam name="T">The compared value type.</typeparam>
    /// <param name="_">The <see cref="DbFunctions"/> instance.</param>
    /// <param name="value1">The first value.</param>
    /// <param name="value2">The second value.</param>
    /// <returns>The smallest value, or <see langword="null"/> when any argument is NULL.</returns>
    public static T Least<T>(this DbFunctions _, T value1, T value2) =>
        throw CreateClientEvaluationException(nameof(Least));

    /// <summary>
    /// Returns the smallest of three values, translated to the StarRocks <c>least()</c>
    /// function. Returns <see langword="null"/> when any argument is <see langword="null"/>
    /// (MySQL semantics; see the class remarks).
    /// </summary>
    /// <typeparam name="T">The compared value type.</typeparam>
    /// <param name="_">The <see cref="DbFunctions"/> instance.</param>
    /// <param name="value1">The first value.</param>
    /// <param name="value2">The second value.</param>
    /// <param name="value3">The third value.</param>
    /// <returns>The smallest value, or <see langword="null"/> when any argument is NULL.</returns>
    public static T Least<T>(this DbFunctions _, T value1, T value2, T value3) =>
        throw CreateClientEvaluationException(nameof(Least));

    /// <summary>
    /// Returns the smallest of four values, translated to the StarRocks <c>least()</c>
    /// function. Returns <see langword="null"/> when any argument is <see langword="null"/>
    /// (MySQL semantics; see the class remarks).
    /// </summary>
    /// <typeparam name="T">The compared value type.</typeparam>
    /// <param name="_">The <see cref="DbFunctions"/> instance.</param>
    /// <param name="value1">The first value.</param>
    /// <param name="value2">The second value.</param>
    /// <param name="value3">The third value.</param>
    /// <param name="value4">The fourth value.</param>
    /// <returns>The smallest value, or <see langword="null"/> when any argument is NULL.</returns>
    public static T Least<T>(this DbFunctions _, T value1, T value2, T value3, T value4) =>
        throw CreateClientEvaluationException(nameof(Least));

    private static InvalidOperationException CreateClientEvaluationException(string method) =>
        new(
            $"EF.Functions.{method} is only intended for use in LINQ queries translated by the "
                + "DotRocks EF Core provider; it cannot be evaluated on the client."
        );
}
