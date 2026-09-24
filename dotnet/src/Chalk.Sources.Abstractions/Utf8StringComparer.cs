using System.Text;

namespace Chalk;

/// <summary>
/// The ordinal comparer for <see cref="Utf8String"/> keys, and — through <see cref="ForString"/> —
/// the one for <see cref="string"/> keys that a batch's bytes can look up. Both implement the
/// alternate-key contracts .NET's collections take, so a <c>Dictionary</c>, <c>HashSet</c> or
/// <c>ConcurrentDictionary</c> built with one answers a <see cref="ReadOnlySpan{T}"/> of UTF-8
/// straight out of a record batch with nothing allocated. A <c>FrozenDictionary</c> keyed by
/// <see cref="Utf8String"/> does too; one keyed by <see cref="string"/> takes an alternate key only
/// under the runtime's own string comparers, so a string-keyed lookup by bytes stays a
/// <c>Dictionary</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ordinal, and only ordinal, as <see cref="Utf8String"/> is: bytes compared as bytes, hashed with
/// <see cref="Utf8String.Hash"/>. A <see cref="string"/> is compared by transcoding it into a stack
/// buffer and hashed over the UTF-8 it encodes to, so the three spellings of one value — a
/// <see cref="string"/>, a <see cref="Utf8String"/> and the bytes — all hash the same and find the
/// same entry.
/// </para>
/// <para>
/// A lookup allocates nothing. An <em>insert</em> through an alternate key has to make the key the
/// dictionary keeps — <c>Create</c> copies the bytes into a <see cref="Utf8String"/> of their own, or
/// decodes them into a <see cref="string"/> — which is the one allocation and the one moment a
/// borrowed value is copied.
/// </para>
/// <example>
/// <code>
/// var weights = new Dictionary&lt;string, double&gt;(Utf8StringComparer.ForString) { ["BTCUSDT"] = 0.4 };
/// var byBytes = weights.GetAlternateLookup&lt;ReadOnlySpan&lt;byte&gt;&gt;();
/// for (var row = 0; row &lt; batch.Length; row++)
/// {
///     if (byBytes.TryGetValue(symbols.GetUtf8(row), out var weight)) { /* no string was made */ }
/// }
/// </code>
/// </example>
/// </remarks>
public sealed class Utf8StringComparer :
    IEqualityComparer<Utf8String>,
    IComparer<Utf8String>,
    IAlternateEqualityComparer<ReadOnlySpan<byte>, Utf8String>,
    IAlternateEqualityComparer<string, Utf8String>
{
    private Utf8StringComparer()
    {
    }

    /// <summary>Keys of <see cref="Utf8String"/>, looked up by a <see cref="Utf8String"/>, a span of UTF-8 or a <see cref="string"/>.</summary>
    public static Utf8StringComparer Ordinal { get; } = new();

    /// <summary>Keys of <see cref="string"/>, looked up by a <see cref="string"/>, a span of UTF-8 or a <see cref="Utf8String"/>.</summary>
    public static StringKeyComparer ForString => StringKeyComparer.Instance;

    /// <inheritdoc />
    public bool Equals(Utf8String x, Utf8String y) => x.Equals(y);

    /// <inheritdoc />
    public int GetHashCode(Utf8String obj) => obj.GetHashCode();

    /// <inheritdoc />
    public int Compare(Utf8String x, Utf8String y) => x.CompareTo(y);

    /// <inheritdoc />
    public bool Equals(ReadOnlySpan<byte> alternate, Utf8String other) => other.Equals(alternate);

    /// <inheritdoc />
    public int GetHashCode(ReadOnlySpan<byte> alternate) => Utf8String.Hash(alternate);

    /// <summary>The key an insert by bytes keeps: a copy, since the bytes were lent.</summary>
    public Utf8String Create(ReadOnlySpan<byte> alternate) => Utf8String.Copy(alternate);

    /// <inheritdoc />
    public bool Equals(string alternate, Utf8String other) => other.TextEquals(alternate);

    /// <inheritdoc />
    public int GetHashCode(string alternate) => Utf8Ordinal.Hash(alternate);

    /// <summary>The key an insert by string keeps: the string encoded.</summary>
    public Utf8String Create(string alternate) => Utf8String.FromString(alternate);

    /// <summary>
    /// The ordinal comparer for <see cref="string"/> keys that a batch's bytes or a
    /// <see cref="Utf8String"/> can look up. Every string is hashed over the UTF-8 it encodes to —
    /// transcoded through a stack buffer, allocating nothing — so an insert or a lookup by
    /// <see cref="string"/> pays a transcode where the default comparer pays none. That is the price
    /// of meeting bytes without a string, and a host whose dictionary is only ever read by bytes is
    /// the host it is for.
    /// </summary>
    public sealed class StringKeyComparer :
        IEqualityComparer<string>,
        IAlternateEqualityComparer<ReadOnlySpan<byte>, string>,
        IAlternateEqualityComparer<Utf8String, string>
    {
        internal static StringKeyComparer Instance { get; } = new();

        private StringKeyComparer()
        {
        }

        /// <inheritdoc />
        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

        /// <inheritdoc />
        public int GetHashCode(string obj) => Utf8Ordinal.Hash(obj);

        /// <inheritdoc />
        public bool Equals(ReadOnlySpan<byte> alternate, string other) => Utf8Ordinal.Equals(alternate, other);

        /// <inheritdoc />
        public int GetHashCode(ReadOnlySpan<byte> alternate) => Utf8String.Hash(alternate);

        /// <summary>The key an insert by bytes keeps: the bytes decoded.</summary>
        public string Create(ReadOnlySpan<byte> alternate) => Encoding.UTF8.GetString(alternate);

        /// <inheritdoc />
        public bool Equals(Utf8String alternate, string other) => alternate.TextEquals(other);

        /// <inheritdoc />
        public int GetHashCode(Utf8String alternate) => alternate.GetHashCode();

        /// <summary>The key an insert by <see cref="Utf8String"/> keeps: the value decoded.</summary>
        public string Create(Utf8String alternate) => alternate.ToString();
    }
}
