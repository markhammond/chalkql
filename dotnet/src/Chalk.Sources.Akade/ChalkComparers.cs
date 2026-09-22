namespace Chalk.Sources.Akade;

/// <summary>
/// Chalk's own value order, as an <see cref="IComparer{T}"/> an Akade index can be built with.
/// </summary>
/// <remarks>
/// <para>
/// An Akade range index sorts its keys with whatever comparer it was given, and Chalk will only
/// treat one as an ORDERED access path when that order is Chalk's: numbers and temporals by value,
/// NaN above every number, strings by code point. The CLR's own defaults are Chalk's for the integer,
/// decimal and temporal types and for nothing else — <c>Comparer&lt;string&gt;.Default</c> is
/// culture-aware, and <c>Comparer&lt;double&gt;.Default</c> puts NaN first — so build the index with
/// one of these and declare it:
/// </para>
/// <code>
/// var set = rows.ToIndexedSet()
///     .WithRangeIndex(SymbolKey, ChalkComparers.For&lt;string&gt;())
///     .Build();
///
/// AkadeSource.From("bars", set).Comparer(SymbolKey, ChalkComparers.For&lt;string&gt;());
/// </code>
/// <para>
/// A compound key works the same way: the comparer for a <c>ValueTuple</c> of admitted types is its
/// components' orders, in key order.
/// </para>
/// <para>
/// A descending comparer makes a descending index, which the planner then serves
/// <c>ORDER BY … DESC</c> from without a sort.
/// </para>
/// </remarks>
public static class ChalkComparers
{
    /// <summary>
    /// Chalk's order for <typeparamref name="T"/>, ascending or descending.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Chalk has no order for this type. A type it cannot compare is a key it cannot describe, and a
    /// comparer that claimed otherwise would be a claim the planner acts on.
    /// </exception>
    public static IComparer<T> For<T>(bool descending = false)
    {
        var comparer = descending
            ? AkadeKeyOrder.Descending<T>()
            : AkadeKeyOrder.Ascending<T>();

        return comparer ?? throw new NotSupportedException(
            $"Chalk has no order for '{typeof(T).FullName ?? typeof(T).Name}'. The types it orders "
            + "are the integer, decimal and temporal ones, float and double, string and Utf8String, "
            + "and a ValueTuple of two to four of those.");
    }
}
