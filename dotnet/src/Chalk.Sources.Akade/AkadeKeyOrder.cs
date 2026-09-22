using Chalk.Sources;

namespace Chalk.Sources.Akade;

/// <summary>
/// Chalk's own order, per admitted key type, as an <see cref="IComparer{T}"/> the adapter can hold
/// and the guard can call once per row without allocating (D280, D281).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SourceValueOrder"/> is the definition, and every comparer here is the same order
/// expressed over the CLR type directly: numbers and temporals by value, NaN above every number,
/// strings by code point. Going through <see cref="SourceValueOrder.Compare"/> itself would box both
/// operands on every comparison, and the ordered path makes one comparison per row.
/// </para>
/// <para>
/// A type that is not here has no Chalk order the adapter is willing to claim, and discovery leaves
/// the index undisclosed rather than guessing one.
/// </para>
/// </remarks>
internal static class AkadeKeyOrder
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object?> Ascendings = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object?> Descendings = new();

    /// <summary>The Chalk order of <typeparamref name="TKey"/>, or null when there is not one.</summary>
    public static IComparer<TKey>? Ascending<TKey>() => (IComparer<TKey>?)Ascending(typeof(TKey));

    /// <summary>
    /// The Chalk order of <paramref name="type"/>, boxed, or null. One instance per type, so a
    /// declared comparer can be recognised as this one by reference (D281).
    /// </summary>
    public static object? Ascending(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Ascendings.GetOrAdd(type, static t => Create(t));
    }

    private static object? Create(Type type)
    {
        if (type == typeof(bool)) return Comparer<bool>.Default;
        if (type == typeof(byte)) return Comparer<byte>.Default;
        if (type == typeof(sbyte)) return Comparer<sbyte>.Default;
        if (type == typeof(short)) return Comparer<short>.Default;
        if (type == typeof(ushort)) return Comparer<ushort>.Default;
        if (type == typeof(int)) return Comparer<int>.Default;
        if (type == typeof(uint)) return Comparer<uint>.Default;
        if (type == typeof(long)) return Comparer<long>.Default;
        if (type == typeof(ulong)) return Comparer<ulong>.Default;
        if (type == typeof(decimal)) return Comparer<decimal>.Default;
        if (type == typeof(DateTime)) return Comparer<DateTime>.Default;
        if (type == typeof(DateTimeOffset)) return Comparer<DateTimeOffset>.Default;
        if (type == typeof(DateOnly)) return Comparer<DateOnly>.Default;
        if (type == typeof(TimeOnly)) return Comparer<TimeOnly>.Default;
        if (type == typeof(TimeSpan)) return Comparer<TimeSpan>.Default;

        // Chalk puts NaN above every number; Comparer<double>.Default puts it below.
        if (type == typeof(float)) return SingleOrder.Instance;
        if (type == typeof(double)) return DoubleOrder.Instance;

        // Chalk's STRING order is the code-point order an uncollated column sorts by, which is what
        // Utf8String compares. The CLR's ordinal order is code *unit* order and disagrees across the
        // surrogate range, so the CLR string is compared here with the surrogate range folded back
        // into place rather than converted.
        if (type == typeof(string)) return StringOrder.Instance;
        if (type == typeof(Utf8String)) return Comparer<Utf8String>.Default;

        return null;
    }

    /// <summary>The reverse of <see cref="Ascending{TKey}"/>, or null when there is not one.</summary>
    public static IComparer<TKey>? Descending<TKey>() => (IComparer<TKey>?)Descending(typeof(TKey));

    /// <summary>The reverse of <see cref="Ascending(Type)"/>, boxed, or null. One per type.</summary>
    public static object? Descending(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return Descendings.GetOrAdd(
            type,
            static t => Ascending(t) is { } ascending
                ? Activator.CreateInstance(typeof(Reversed<>).MakeGenericType(t), ascending)
                : null);
    }

    /// <summary>Whether Chalk has an order for this type at all.</summary>
    public static bool HasOrder(Type type) => Ascending(type) is not null;

    /// <summary>
    /// Whether equality on this type is the exact equality a HASH index has to answer: the CLR's
    /// equality and Chalk's agree on every value. The reals are out, because Chalk orders NaN and
    /// the CLR's two zeroes and NaNs do not agree with that; everything else Chalk can compare is in,
    /// GUID included — it has exact equality, and only its <em>order</em> is not Chalk's.
    /// </summary>
    public static bool IsHashKeyType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type == typeof(float) || type == typeof(double))
        {
            return false;
        }

        return type == typeof(Guid) || HasOrder(type);
    }

    /// <summary>
    /// Whether an ORDERED index over this type is admitted on the strength of the type alone: the
    /// CLR's default order <em>is</em> Chalk's (D281). The integer and decimal types, and the
    /// temporal ones.
    /// </summary>
    public static bool IsOrderedKeyType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly)
            || type == typeof(TimeSpan);
    }

    /// <summary>
    /// Whether an ORDERED index over this type is admitted only once the host has declared the
    /// comparer the Akade index was built with (D281): the reals, whose NaN placement the CLR does
    /// not share, and the strings, whose CLR order is culture-aware.
    /// </summary>
    public static bool NeedsDeclaredComparer(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type == typeof(float)
            || type == typeof(double)
            || type == typeof(string)
            || type == typeof(Utf8String);
    }

    /// <summary>
    /// The value of <paramref name="type"/> below every other, for the components of a compound key
    /// a range does not bound (D280).
    /// </summary>
    public static object? Minimum(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type == typeof(bool)) return false;
        if (type == typeof(byte)) return byte.MinValue;
        if (type == typeof(sbyte)) return sbyte.MinValue;
        if (type == typeof(short)) return short.MinValue;
        if (type == typeof(ushort)) return ushort.MinValue;
        if (type == typeof(int)) return int.MinValue;
        if (type == typeof(uint)) return uint.MinValue;
        if (type == typeof(long)) return long.MinValue;
        if (type == typeof(ulong)) return ulong.MinValue;
        if (type == typeof(decimal)) return decimal.MinValue;
        if (type == typeof(DateTime)) return DateTime.MinValue;
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.MinValue;
        if (type == typeof(DateOnly)) return DateOnly.MinValue;
        if (type == typeof(TimeOnly)) return TimeOnly.MinValue;
        if (type == typeof(TimeSpan)) return TimeSpan.MinValue;
        if (type == typeof(float)) return float.NegativeInfinity;
        if (type == typeof(double)) return double.NegativeInfinity;
        if (type == typeof(string)) return string.Empty;
        if (type == typeof(Utf8String)) return Utf8String.Empty;

        return null;
    }

    /// <summary>
    /// The value of <paramref name="type"/> above every other. NaN for the reals, because that is
    /// where Chalk puts it; the largest code point for a string, which is the sentinel the sample
    /// uses and the conformance kit is what would catch a key sorting past it.
    /// </summary>
    public static object? Maximum(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type == typeof(bool)) return true;
        if (type == typeof(byte)) return byte.MaxValue;
        if (type == typeof(sbyte)) return sbyte.MaxValue;
        if (type == typeof(short)) return short.MaxValue;
        if (type == typeof(ushort)) return ushort.MaxValue;
        if (type == typeof(int)) return int.MaxValue;
        if (type == typeof(uint)) return uint.MaxValue;
        if (type == typeof(long)) return long.MaxValue;
        if (type == typeof(ulong)) return ulong.MaxValue;
        if (type == typeof(decimal)) return decimal.MaxValue;
        if (type == typeof(DateTime)) return DateTime.MaxValue;
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.MaxValue;
        if (type == typeof(DateOnly)) return DateOnly.MaxValue;
        if (type == typeof(TimeOnly)) return TimeOnly.MaxValue;
        if (type == typeof(TimeSpan)) return TimeSpan.MaxValue;
        if (type == typeof(float)) return float.NaN;
        if (type == typeof(double)) return double.NaN;
        if (type == typeof(string)) return LargestString;
        if (type == typeof(Utf8String)) return LargestUtf8;

        return null;
    }

    /// <summary>
    /// U+10FFFF, the largest code point there is, repeated: no text sorts above it under code-point
    /// order, which is the order every string comparer this package admits produces.
    /// </summary>
    private const string LargestString = "\U0010FFFF\U0010FFFF";

    private static readonly Utf8String LargestUtf8 = Utf8String.FromString(LargestString);

    /// <summary>Chalk's code-point order over a CLR string, without allocating.</summary>
    /// <remarks>
    /// UTF-16 stores a code point above U+FFFF as a surrogate pair whose units sit at U+D800..U+DFFF,
    /// <em>below</em> the private-use and specials blocks at U+E000..U+FFFF. Comparing code units
    /// therefore puts a supplementary code point below U+E000, where code-point order — and UTF-8
    /// byte order, and <see cref="Utf8String.CompareTo"/> — put it above. Ranking a unit by
    /// <c>c &lt; 0xD800 ? c : c &lt; 0xE000 ? c + 0x2000 : c - 0x800</c> restores the three blocks to
    /// code-point order without touching the bytes.
    /// </remarks>
    internal sealed class StringOrder : IComparer<string>
    {
        public static readonly StringOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return x is null && y is null ? 0 : x is null ? -1 : 1;
            }

            var shared = Math.Min(x.Length, y.Length);
            for (var i = 0; i < shared; i++)
            {
                var left = Rank(x[i]);
                var right = Rank(y[i]);
                if (left != right)
                {
                    return left < right ? -1 : 1;
                }
            }

            return x.Length.CompareTo(y.Length);
        }

        private static int Rank(char value) =>
            value < '\uD800' ? value : value < '' ? value + 0x2000 : value - 0x800;
    }

    private sealed class SingleOrder : IComparer<float>
    {
        public static readonly SingleOrder Instance = new();

        public int Compare(float x, float y)
        {
            if (float.IsNaN(x))
            {
                return float.IsNaN(y) ? 0 : 1;
            }

            return float.IsNaN(y) ? -1 : x < y ? -1 : x > y ? 1 : 0;
        }
    }

    private sealed class DoubleOrder : IComparer<double>
    {
        public static readonly DoubleOrder Instance = new();

        public int Compare(double x, double y)
        {
            if (double.IsNaN(x))
            {
                return double.IsNaN(y) ? 0 : 1;
            }

            return double.IsNaN(y) ? -1 : x < y ? -1 : x > y ? 1 : 0;
        }
    }

    internal sealed class Reversed<TKey> : IComparer<TKey>
    {
        private readonly IComparer<TKey> _ascending;

        public Reversed(IComparer<TKey> ascending) => _ascending = ascending;

        public int Compare(TKey? x, TKey? y) => _ascending.Compare(y!, x!);
    }
}
