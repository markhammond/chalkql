using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;

namespace Chalk.Catalog.Tests;

/// <summary>
/// <c>Utf8StringComparer</c>: a dictionary keyed by <c>Utf8String</c> or by <c>string</c> answers a
/// span of UTF-8 straight out of a batch — the three spellings of one value hash the same and find
/// the same entry — and only an insert through an alternate key copies anything.
/// </summary>
public sealed class Utf8StringComparerTests
{
    private static readonly string[] Words = ["café", "日経", "ADA", "A_VERY_LONG_SYMBOL_NAME", "naïve"];

    [Fact]
    public void Utf8String_keys_are_found_by_span_and_by_string()
    {
        var weights = new Dictionary<Utf8String, int>(Utf8StringComparer.Ordinal);
        for (var i = 0; i < Words.Length; i++)
        {
            weights[Utf8String.FromString(Words[i])] = i;
        }

        var bySpan = weights.GetAlternateLookup<ReadOnlySpan<byte>>();
        var byString = weights.GetAlternateLookup<string>();

        Assert.Equal(0, bySpan["café"u8]);
        Assert.Equal(1, bySpan["日経"u8]);
        Assert.True(bySpan.TryGetValue("ADA"u8, out var ada));
        Assert.Equal(2, ada);
        Assert.False(bySpan.TryGetValue("cafe"u8, out _));
        Assert.False(bySpan.TryGetValue("ada"u8, out _));
        Assert.Equal(4, byString["naïve"]);
        Assert.False(byString.TryGetValue("Naïve", out _));

        // The bytes may live anywhere: a slice of a larger buffer finds the same entry.
        ReadOnlySpan<byte> buffer = [0xFF, .. "日経"u8, 0xFF];
        Assert.Equal(1, bySpan[buffer[1..^1]]);
    }

    [Fact]
    public void An_insert_by_span_keeps_a_copy_not_the_lent_bytes()
    {
        var seen = new Dictionary<Utf8String, int>(Utf8StringComparer.Ordinal);
        var lookup = seen.GetAlternateLookup<ReadOnlySpan<byte>>();

        var lent = "zebra"u8.ToArray();
        Assert.True(lookup.TryAdd(lent, 1));
        lent[0] = (byte)'Z';

        Assert.True(seen.ContainsKey(Utf8String.FromString("zebra")));
        Assert.False(seen.ContainsKey(Utf8String.FromString("Zebra")));
        Assert.Equal(1, lookup["zebra"u8]);
    }

    [Fact]
    public void String_keys_are_found_by_span_and_by_Utf8String()
    {
        var weights = new Dictionary<string, int>(Utf8StringComparer.ForString);
        for (var i = 0; i < Words.Length; i++)
        {
            weights[Words[i]] = i;
        }

        var bySpan = weights.GetAlternateLookup<ReadOnlySpan<byte>>();
        var byUtf8 = weights.GetAlternateLookup<Utf8String>();

        Assert.Equal(0, bySpan["café"u8]);
        Assert.Equal(3, bySpan["A_VERY_LONG_SYMBOL_NAME"u8]);
        Assert.False(bySpan.TryGetValue("cafe"u8, out _));
        Assert.False(bySpan.TryGetValue("café "u8, out _));
        Assert.Equal(1, byUtf8[Utf8String.FromString("日経")]);
        Assert.False(byUtf8.TryGetValue(Utf8String.FromString("日"), out _));

        // The ordinary path is unchanged: a string finds a string, and only the same string.
        Assert.Equal(4, weights["naïve"]);
        Assert.False(weights.ContainsKey("NAÏVE"));

        // An insert by bytes keeps a decoded string, which the string side then finds.
        Assert.True(bySpan.TryAdd("München"u8, 5));
        Assert.Equal(5, weights["München"]);
    }

    [Fact]
    public void A_string_that_is_not_valid_utf16_is_a_key_no_bytes_can_find()
    {
        var lone = "\ud800x";
        var weights = new Dictionary<string, int>(Utf8StringComparer.ForString) { [lone] = 1 };
        var bySpan = weights.GetAlternateLookup<ReadOnlySpan<byte>>();

        // The string side still works, and the key hashes deterministically.
        Assert.Equal(1, weights[lone]);
        Assert.Equal(
            Utf8StringComparer.ForString.GetHashCode(lone),
            Utf8StringComparer.ForString.GetHashCode(new string(lone.AsSpan())));

        // No UTF-8 encodes a lone surrogate, so no bytes find it — not even the replacement's.
        Assert.False(bySpan.TryGetValue(Encoding.UTF8.GetBytes(lone), out _));
        Assert.False("x"u8.TextEquals(lone));
    }

    [Fact]
    public void The_three_spellings_of_a_value_hash_the_same()
    {
        foreach (var word in Words.Append(new string('é', 300)).Append(string.Empty))
        {
            var bytes = Encoding.UTF8.GetBytes(word);
            var expected = Utf8String.Hash(bytes);

            Assert.Equal(expected, Utf8StringComparer.ForString.GetHashCode(word));
            Assert.Equal(expected, Utf8StringComparer.Ordinal.GetHashCode(word));
            Assert.Equal(expected, Utf8StringComparer.Ordinal.GetHashCode(Utf8String.FromString(word)));
            Assert.Equal(expected, Utf8StringComparer.ForString.GetHashCode(Utf8String.FromString(word)));
            Assert.Equal(expected, ((ReadOnlySpan<byte>)bytes).Utf8Hash());
            Assert.Equal(expected, Utf8StringComparer.Ordinal.GetHashCode((ReadOnlySpan<byte>)bytes));
        }
    }

    [Fact]
    public void Text_equality_on_a_span_is_ordinal_and_trusts_no_byte()
    {
        Assert.True("café"u8.TextEquals("café"));
        Assert.True("ADA"u8.TextEquals("ADA"));
        Assert.False("ADA"u8.TextEquals("ada"));
        Assert.False("cafe"u8.TextEquals("café"));
        Assert.False("café"u8.TextEquals(null));
        Assert.True(ReadOnlySpan<byte>.Empty.TextEquals(string.Empty));

        // A byte outside ASCII never matches a code unit of the same value: 0xE9 is not "é".
        ReadOnlySpan<byte> notUtf8 = [0xE9];
        Assert.False(notUtf8.TextEquals("é"));
    }

    [Fact]
    public void Every_collection_with_an_alternate_lookup_takes_the_comparers()
    {
        var set = new HashSet<string>(Words, Utf8StringComparer.ForString);
        Assert.True(set.GetAlternateLookup<ReadOnlySpan<byte>>().Contains("日経"u8));
        Assert.False(set.GetAlternateLookup<ReadOnlySpan<byte>>().Contains("日本"u8));

        // A frozen dictionary keyed by Utf8String takes the alternate lookup; one keyed by string
        // with a comparer of its own does not — the runtime's string-keyed frozen dictionaries take
        // an alternate key only under their own comparers — so a string-keyed lookup by bytes stays a
        // Dictionary, which the comparer's doc says.
        var frozenUtf8 = Words.Select((w, i) => (Utf8String.FromString(w), i))
            .ToFrozenDictionary(p => p.Item1, p => p.i, Utf8StringComparer.Ordinal);
        Assert.Equal(2, frozenUtf8.GetAlternateLookup<ReadOnlySpan<byte>>()["ADA"u8]);
        var frozenString = Words.Select((w, i) => (w, i)).ToFrozenDictionary(p => p.w, p => p.i, Utf8StringComparer.ForString);
        Assert.Equal(2, frozenString["ADA"]);
        Assert.Throws<InvalidOperationException>(() => frozenString.GetAlternateLookup<ReadOnlySpan<byte>>());

        var concurrent = new ConcurrentDictionary<Utf8String, int>(Utf8StringComparer.Ordinal);
        concurrent[Utf8String.FromString("café")] = 7;
        Assert.Equal(7, concurrent.GetAlternateLookup<ReadOnlySpan<byte>>()["café"u8]);
        Assert.Equal(7, concurrent.GetAlternateLookup<string>()["café"]);
    }

    [Fact]
    public void Ordinal_orders_bytes_as_Utf8String_does()
    {
        var sorted = Words.Select(Utf8String.FromString).Order(Utf8StringComparer.Ordinal).Select(u => u.ToString());
        Assert.Equal(Words.OrderBy(w => w, StringComparer.Ordinal).ToArray(), sorted.ToArray());
    }
}
