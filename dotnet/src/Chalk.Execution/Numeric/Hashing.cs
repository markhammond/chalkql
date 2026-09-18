using System.Runtime.CompilerServices;

namespace Chalk.Execution.Numeric;

/// <summary>
/// The hash the aggregate's open-addressing table and the IN-list set use (§6.6). Deliberately its
/// own function rather than <c>GetHashCode</c>: grouping must hash NULL to a fixed salt and must give
/// the same answer for the same bytes on every run, so that first-seen group order is reproducible.
/// </summary>
internal static class Hashing
{
    /// <summary>What a NULL grouping key hashes to.</summary>
    public const ulong NullSalt = 0x9E37_79B9_7F4A_7C15UL;

    /// <summary>splitmix64's finaliser: cheap, and good enough that probe chains stay short.</summary>
    public static ulong Mix(ulong value)
    {
        value += 0x9E37_79B9_7F4A_7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>Folds one more hash into a running one, so a multi-key group hashes in key order.</summary>
    public static ulong Combine(ulong accumulated, ulong next) =>
        Mix(accumulated ^ (next + 0x9E37_79B9_7F4A_7C15UL + (accumulated << 6) + (accumulated >> 2)));

    /// <summary>FNV-1a over bytes, for STRING, BINARY and UUID keys.</summary>
    public static ulong Bytes(ReadOnlySpan<byte> value)
    {
        var hash = 0xCBF2_9CE4_8422_2325UL;
        foreach (var b in value)
        {
            hash = (hash ^ b) * 0x0000_0100_0000_01B3UL;
        }

        return Mix(hash);
    }

    /// <summary>
    /// The folded 128-bit product of two words: one multiply and one XOR, and every input bit reaches
    /// every output bit. This is the multiply-mix the aggregate's key hashing is built out of (D255).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Fold(ulong left, ulong right)
    {
        var high = Math.BigMul(left, right, out var low);
        return low ^ high;
    }

    /// <summary>
    /// A grouping key hashed from the two words and the length of its image (D255, <c>KeyImage</c>).
    /// A fixed-width key is one or two words and needs no byte loop at all; a short string is its
    /// bytes packed into the same two words, so both hash through one shape.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Words(ulong low, ulong high, int length) =>
        Mix(Fold(low ^ 0x9E37_79B9_7F4A_7C15UL, high ^ 0xBF58_476D_1CE4_E5B9UL)
            ^ ((ulong)(uint)length * 0x94D0_49BB_1331_11EBUL));
}
