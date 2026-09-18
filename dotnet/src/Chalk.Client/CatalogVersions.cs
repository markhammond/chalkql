using System.Security.Cryptography;

namespace Chalk.Client;

/// <summary>
/// The versions the engine mints (D271 (a), <c>docs/design/44-catalog-registration.md</c> §1): an
/// instance version per <see cref="ChalkEngine"/>, a shape version when some table's shape changed,
/// and a statistics version when a refresh moved any table's numbers.
/// </summary>
/// <remarks>
/// <para>
/// A ULID: a 48-bit millisecond timestamp and 80 random bits, in Crockford base32, twenty-six
/// characters, lexicographically ordered by time. Unique across engines and across one engine's
/// restarts, so a sidecar shared by several engines keys its registry by version and never confuses
/// two; time-ordered, so the planner can tell a statistics message that arrived out of order from a
/// newer one without a second field.
/// </para>
/// <para>
/// Minted here rather than taken from a package: it is thirty lines, the format is fixed by the ULID
/// specification, and a dependency the host would inherit is a poor trade for that. Monotonic within
/// a millisecond — the random half is incremented rather than redrawn when two versions are minted in
/// the same millisecond — so two versions minted in one refresh cycle still sort in the order they
/// were minted, which is what makes the planner's "at or before the one held" test correct.
/// </para>
/// <para>
/// A host never sees one of these unless it logs it: nothing on <see cref="ChalkEngine"/>'s surface
/// takes a version, and the epoch a refresh returns is still the engine's own counter.
/// </para>
/// </remarks>
internal static class CatalogVersions
{
    /// <summary>Crockford base32: no I, L, O or U, so a version read aloud cannot be mistyped.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly Lock Gate = new();
    private static long _lastMillis;
    private static readonly byte[] LastRandom = new byte[10];

    /// <summary>A new version, later than every version this process has minted before it.</summary>
    public static string Mint()
    {
        Span<byte> value = stackalloc byte[16];
        lock (Gate)
        {
            var millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (millis > _lastMillis)
            {
                _lastMillis = millis;
                RandomNumberGenerator.Fill(LastRandom);
            }
            else
            {
                // The same millisecond, or a clock that stepped backwards: carry the last timestamp
                // and increment the random half, so the result still sorts after its predecessor.
                Increment(LastRandom);
            }

            var timestamp = _lastMillis;
            for (var i = 5; i >= 0; i--)
            {
                value[i] = (byte)(timestamp & 0xFF);
                timestamp >>= 8;
            }

            LastRandom.CopyTo(value[6..]);
        }

        return Encode(value);
    }

    /// <summary>
    /// Adds one to the 80-bit random half, from the least significant byte up. An all-ones half wraps
    /// to zero, which cannot collide with anything this process has minted: the timestamp would have
    /// to be the same millisecond and 2^80 versions would have to have been minted inside it.
    /// </summary>
    private static void Increment(byte[] random)
    {
        for (var i = random.Length - 1; i >= 0; i--)
        {
            if (++random[i] != 0)
            {
                return;
            }
        }
    }

    /// <summary>The sixteen bytes as twenty-six Crockford base32 characters, most significant first.</summary>
    private static string Encode(ReadOnlySpan<byte> value)
    {
        Span<char> text = stackalloc char[26];

        // The timestamp's 48 bits are ten characters with two spare bits at the top, which the ULID
        // specification fixes at zero; the remaining sixteen characters are the 80 random bits.
        long timestamp = 0;
        for (var i = 0; i < 6; i++)
        {
            timestamp = (timestamp << 8) | value[i];
        }

        for (var i = 9; i >= 0; i--)
        {
            text[i] = Alphabet[(int)(timestamp & 31)];
            timestamp >>= 5;
        }

        ulong high = 0;
        for (var i = 6; i < 11; i++)
        {
            high = (high << 8) | value[i];
        }

        ulong low = 0;
        for (var i = 11; i < 16; i++)
        {
            low = (low << 8) | value[i];
        }

        for (var i = 7; i >= 0; i--)
        {
            text[10 + i] = Alphabet[(int)(high & 31)];
            high >>= 5;
        }

        for (var i = 7; i >= 0; i--)
        {
            text[18 + i] = Alphabet[(int)(low & 31)];
            low >>= 5;
        }

        return new string(text);
    }
}
