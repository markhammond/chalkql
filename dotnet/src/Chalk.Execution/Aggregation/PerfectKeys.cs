using Chalk.Execution.Numeric;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// The function a perfect key table indexes with: a multiplicative hash of a selector, into a table
/// of at least twice the key count (D255). Every field is a value the lookup loads, so the lookup is
/// a multiply, a shift and an array read.
/// </summary>
/// <remarks>
/// For a fixed-width key the selector is the key image's words; for an inline string key it is the
/// key's length and at most two of its bytes — the <c>gperf --switch</c> idea Hashify uses for tiny
/// maps, as informed by the user — so a lookup reads three bytes of the key rather than all of it.
/// </remarks>
internal readonly struct PerfectKeyFunction
{
    public PerfectKeyFunction(ulong seed, int shift, int position1, int position2, bool strings)
    {
        Seed = seed;
        Shift = shift;
        Position1 = position1;
        Position2 = position2;
        Strings = strings;
    }

    public ulong Seed { get; }

    /// <summary>64 minus the table's bit width: the top bits of the product are the slot.</summary>
    public int Shift { get; }

    public int Position1 { get; }

    public int Position2 { get; }

    /// <summary>Whether the selector is a string's length and bytes rather than the image's words.</summary>
    public bool Strings { get; }
}

/// <summary>
/// Builds the perfect hash of D255 over a key set a source's statistics declared, once per execution
/// and within a fixed budget. A failure is not an error: the aggregate simply stays on its general
/// table, which is also what happens to any key the declared set turned out not to contain.
/// </summary>
internal static class PerfectKeys
{
    /// <summary>The largest declared distinct count this is attempted for (design 33 §1).</summary>
    public const int MaxDistinct = 4096;

    /// <summary>How many multipliers one selector is tried with before the search moves on.</summary>
    private const int SeedsPerSelector = 24;

    /// <summary>The whole search's budget, in placement attempts.</summary>
    private const int MaxAttempts = 512;

    /// <summary>
    /// Fills <paramref name="slots"/> with the group id each key hashes to, or returns false when no
    /// function inside the budget separates the keys.
    /// </summary>
    /// <param name="low">Each group's image low word, indexed by group id.</param>
    /// <param name="high">Each group's image high word.</param>
    /// <param name="lengths">Each group's image length; a NULL group's is not a key and is skipped.</param>
    /// <param name="groupCount">How many groups there are.</param>
    /// <param name="store">The key store, for the bytes an inline string key is separated by.</param>
    /// <param name="strings">Whether the key is a variable-length one.</param>
    /// <param name="slots">The table, at least <paramref name="slotCount"/> long.</param>
    /// <param name="slotCount">The table's size, a power of two of at least twice the key count.</param>
    /// <param name="function">The function found, when this returns true.</param>
    public static bool TryBuild(
        ReadOnlySpan<ulong> low,
        ReadOnlySpan<ulong> high,
        ReadOnlySpan<int> lengths,
        int groupCount,
        GroupKeyStore store,
        bool strings,
        int[] slots,
        int slotCount,
        out PerfectKeyFunction function)
    {
        function = default;
        var shift = 64 - System.Numerics.BitOperations.TrailingZeroCount((uint)slotCount);
        var attempts = 0;

        if (!strings)
        {
            for (var s = 0; s < SeedsPerSelector && attempts < MaxAttempts; s++, attempts++)
            {
                var seed = Seed(s);
                if (Place(low, high, lengths, groupCount, store, false, 0, 0, seed, shift, slots, slotCount))
                {
                    function = new PerfectKeyFunction(seed, shift, 0, 0, strings: false);
                    return true;
                }
            }

            return false;
        }

        // Every key has to fit its image for the byte positions to be a separator at all: a longer
        // one would be validated by a compare the image cannot make.
        var longest = 0;
        for (var group = 0; group < groupCount; group++)
        {
            var length = lengths[group];
            if (length == KeyImage.NullLength)
            {
                continue;
            }

            if (length > KeyImage.MaxInline)
            {
                return false;
            }

            longest = Math.Max(longest, length);
        }

        var positions = Math.Max(1, longest);
        for (var p1 = 0; p1 < positions; p1++)
        {
            for (var p2 = p1; p2 < positions; p2++)
            {
                for (var s = 0; s < SeedsPerSelector; s++)
                {
                    if (attempts++ >= MaxAttempts)
                    {
                        return false;
                    }

                    var seed = Seed(s);
                    if (Place(low, high, lengths, groupCount, store, true, p1, p2, seed, shift, slots, slotCount))
                    {
                        function = new PerfectKeyFunction(seed, shift, p1, p2, strings: true);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>The selector of one group's key, which is what the multiplier is applied to.</summary>
    public static ulong Selector(ulong low, ulong high) => low ^ high;

    /// <summary>The selector of an inline string key: its length and at most two of its bytes.</summary>
    public static ulong Selector(ReadOnlySpan<byte> key, int position1, int position2)
    {
        var length = key.Length;
        ulong first = position1 < length ? key[position1] : (byte)0;
        ulong second = position2 < length ? key[position2] : (byte)0;
        return (ulong)(uint)length | (first << 20) | (second << 36);
    }

    /// <summary>The slot a selector lands in.</summary>
    public static int Slot(ulong selector, ulong seed, int shift) => (int)((selector * seed) >> shift);

    /// <summary>
    /// Tries to place every key in its own slot. The table doubles as the search's own scratch: it is
    /// refilled with -1 at the start of each attempt, so the search allocates nothing at all.
    /// </summary>
    private static bool Place(
        ReadOnlySpan<ulong> low,
        ReadOnlySpan<ulong> high,
        ReadOnlySpan<int> lengths,
        int groupCount,
        GroupKeyStore store,
        bool strings,
        int position1,
        int position2,
        ulong seed,
        int shift,
        int[] slots,
        int slotCount)
    {
        Array.Fill(slots, -1, 0, slotCount);
        for (var group = 0; group < groupCount; group++)
        {
            if (lengths[group] == KeyImage.NullLength)
            {
                continue;
            }

            var selector = strings
                ? Selector(store.Key(group), position1, position2)
                : Selector(low[group], high[group]);
            var slot = Slot(selector, seed, shift);
            if (slots[slot] >= 0)
            {
                return false;
            }

            slots[slot] = group;
        }

        return true;
    }

    /// <summary>
    /// The multipliers the search tries, in a fixed order so that a plan builds the same function on
    /// every run. Odd, and spread across the word, which is what a multiplicative hash wants.
    /// </summary>
    private static ulong Seed(int index) => Hashing.Mix((ulong)index + 1) | 1UL;
}
