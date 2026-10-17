namespace Chalk.Sources;

/// <summary>
/// Chalk's order over a CLR string: by code point, the order <see cref="Utf8String.CompareTo"/>
/// keeps by byte, so a key held as a string and the same key held as UTF-8 sort alike.
/// </summary>
/// <remarks>
/// UTF-16 stores a code point above U+FFFF as a surrogate pair whose units sit at U+D800..U+DFFF,
/// <em>below</em> the private-use and specials blocks at U+E000..U+FFFF, so comparing code units, as
/// <see cref="string.CompareOrdinal(string, string)"/> does, puts a supplementary code point below
/// U+E000 where code-point order puts it above. The two orders part only at the first unit that
/// differs, so that unit is found as an ordinal comparison finds it, and only the two units there
/// are ranked again: <c>c &lt; 0xD800 ? c : c &lt; 0xE000 ? c + 0x2000 : c - 0x800</c> restores the
/// three blocks to code-point order without touching the text.
/// </remarks>
internal static class TextOrder
{
    /// <summary>Orders two strings by code point: negative, zero or positive.</summary>
    public static int Compare(string left, string right) => Compare(left.AsSpan(), right.AsSpan());

    /// <inheritdoc cref="Compare(string, string)"/>
    public static int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var shared = left.CommonPrefixLength(right);
        if (shared == left.Length || shared == right.Length)
        {
            return left.Length.CompareTo(right.Length);
        }

        return Rank(left[shared]) < Rank(right[shared]) ? -1 : 1;
    }

    /// <summary>The same order as a comparer, for a sort or a sorted collection.</summary>
    public static IComparer<string> Comparer { get; } = new TextComparer();

    /// <summary>
    /// A unit's place in code-point order: the surrogates above U+E000..U+FFFF. Comparing two texts
    /// unit by unit by this rank, up to the first that differs, is <see cref="Compare(string, string)"/>.
    /// </summary>
    internal static int Rank(char unit) =>
        unit < '\uD800' ? unit : unit < '\uE000' ? unit + 0x2000 : unit - 0x800;

    /// <summary>A null string before any text, as an ordinal comparer puts it.</summary>
    private sealed class TextComparer : IComparer<string>
    {
        public int Compare(string? x, string? y) =>
            x is null || y is null
                ? (x is null ? (y is null ? 0 : -1) : 1)
                : TextOrder.Compare(x, y);
    }
}
