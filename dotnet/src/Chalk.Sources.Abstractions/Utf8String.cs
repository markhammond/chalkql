using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;

namespace Chalk;

/// <summary>
/// A run of UTF-8 bytes with ordinal semantics: the marker type that says "this is text" without
/// making a .NET string (D144, <c>docs/design/24-zero-gc.md</c> §2).
/// </summary>
/// <remarks>
/// <para>
/// Everything inside Chalk is already UTF-8 — a STRING column is Arrow UTF-8 in arena buffers, and a
/// kernel reads a row as a <c>ReadOnlySpan&lt;byte&gt;</c>. What used to force an allocation was the
/// boundary: a POCO property, a Tier 1 delegate parameter or a host reading a result had to spell
/// text as <c>string</c>, and a string has to be encoded or decoded. A <see cref="Utf8String"/> is
/// that same boundary spelled in the bytes both sides already hold.
/// </para>
/// <para><b>Ordinal, and only ordinal.</b> <see cref="Equals(Utf8String)"/>,
/// <see cref="GetHashCode"/> and <see cref="CompareTo"/> compare bytes — the same order as Chalk's
/// binary collation and as Arrow's, and the same order <c>ORDER BY</c> gives an uncollated STRING
/// column. This is not a <c>string</c> replacement where culture matters: casing, culture collations
/// and <c>LIKE</c> with non-ASCII folding stay on the existing kernels, which decode when they must.
/// </para>
/// <para>
/// <b>Lifetime.</b> Nothing the engine hands a host is a borrowed <see cref="Utf8String"/>: a STRING
/// cell is read as a <c>ReadOnlySpan&lt;byte&gt;</c> through <c>GetUtf8</c>, a Tier 1 delegate is
/// handed its STRING argument as one, and a Tier 2 kernel reads <c>ColumnView.VarValue</c> — spans the
/// compiler keeps inside the batch or the call. A value to keep is copied out with
/// <c>ToUtf8String()</c>, and a composite read back as a record copies its text fields. Every
/// <see cref="Utf8String"/> a host holds is therefore the host's own memory and outlives anything.
/// </para>
/// <para>
/// <b>One trap, and it is the price of the implicit conversions.</b> Because a
/// <see cref="Utf8String"/> converts implicitly from <c>byte[]</c>, a <c>null</c> literal in the
/// other arm of a conditional binds to <em>that</em> conversion and produces the empty value rather
/// than a null <c>Utf8String?</c>: <c>Utf8String? x = flag ? value : null;</c> is always non-null.
/// Write the NULL out — <c>Utf8String? x = default; if (flag) { x = value; }</c> — wherever a value
/// may be absent. Chalk's own code does, and the tests pin it.
/// </para>
/// <para>
/// <b>Validation.</b> The struct itself never validates: wrapping bytes is free and a value that
/// came out of a column is valid by construction. Bytes are checked with
/// <see cref="Utf8.IsValid(ReadOnlySpan{byte})"/> exactly where they <em>enter</em> a column — the
/// POCO chunk writer and the Tier 1 result path — so an Arrow buffer never holds invalid UTF-8, and
/// nowhere else, so reading never pays for it.
/// </para>
/// </remarks>
public readonly struct Utf8String : IEquatable<Utf8String>, IComparable<Utf8String>
{
    private readonly ReadOnlyMemory<byte> _bytes;

    /// <summary>Wraps <paramref name="bytes"/> without copying or validating them.</summary>
    /// <remarks>
    /// The value borrows <paramref name="bytes"/>: it is valid exactly as long as that memory is.
    /// </remarks>
    public Utf8String(ReadOnlyMemory<byte> bytes) => _bytes = bytes;

    /// <summary>The empty string, which is not the same thing as a NULL.</summary>
    public static Utf8String Empty => default;

    /// <summary>The length in <b>bytes</b>, which is not the number of characters.</summary>
    public int Length => _bytes.Length;

    /// <summary>Whether this is the empty string.</summary>
    public bool IsEmpty => _bytes.IsEmpty;

    /// <summary>Whether these bytes are valid UTF-8. Vectorised, and allocates nothing.</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes) => Utf8.IsValid(bytes);

    /// <summary>
    /// Wraps a byte array without copying or validating it. The value is valid as long as the array
    /// is; an array the caller then mutates changes the value.
    /// </summary>
    public static implicit operator Utf8String(byte[]? bytes) => new(bytes.AsMemory());

    /// <summary>
    /// Wraps memory without copying or validating it. The value borrows that memory and is valid
    /// exactly as long as it is.
    /// </summary>
    public static implicit operator Utf8String(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>Wraps a byte array without copying or validating it (the named form of the operator).</summary>
    /// <remarks>The value borrows the array and is valid as long as it is.</remarks>
    public static Utf8String FromBytes(byte[]? bytes) => new(bytes.AsMemory());

    /// <summary>Wraps memory without copying or validating it (the named form of the operator).</summary>
    /// <remarks>The value borrows that memory and is valid exactly as long as it is.</remarks>
    public static Utf8String FromBytes(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>
    /// Encodes a .NET string. This allocates, by the host's choice: it is the one direction that
    /// cannot avoid it, and it is a host's own memory, so the result outlives every batch.
    /// </summary>
    public static Utf8String FromString(string? value) =>
        string.IsNullOrEmpty(value) ? default : new Utf8String(Encoding.UTF8.GetBytes(value));

    public static Utf8String FromAsciiString(string? value) =>
        string.IsNullOrEmpty(value) ? default : new Utf8String(Encoding.ASCII.GetBytes(value));

    /// <summary>
    /// Copies <paramref name="bytes"/> into memory of this value's own, so the result outlives the
    /// span it came from. This allocates; wrapping is <see cref="FromBytes(ReadOnlyMemory{byte})"/>.
    /// </summary>
    public static Utf8String Copy(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? default : new Utf8String(bytes.ToArray());

    /// <summary>Whether this value's bytes are valid UTF-8. Vectorised, and allocates nothing.</summary>
    public bool IsValid() => Utf8.IsValid(_bytes.Span);

    /// <summary>
    /// Returns a read-only span over the UTF-8 bytes without copying.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> AsSpan() => _bytes.Span;

    /// <summary>
    /// Returns a read-only memory view over the UTF-8 bytes without copying.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlyMemory<byte> AsMemory() => _bytes;

    /// <summary>
    /// A copy of the bytes in a new array, which the caller owns and which outlives every batch.
    /// </summary>
    public byte[] ToArray() => _bytes.ToArray();

    /// <summary>
    /// Decodes to a .NET string. <b>The one place a string is made</b>, and the host is the one that
    /// calls it. Invalid bytes decode to U+FFFD the way <see cref="Encoding.UTF8"/> does, because a
    /// value that reached here came out of a validated column.
    /// </summary>
    public override string ToString() => Encoding.UTF8.GetString(_bytes.Span);

    /// <summary>
    /// A deterministic hash of the bytes: <see cref="Hash"/> over <see cref="Span"/>. Two values with
    /// the same bytes in different memory hash the same, which is what a dictionary keyed on one
    /// needs, and the same bytes hash the same in every process and on every machine, which is what
    /// a host persisting or comparing a hash across processes needs (D144).
    /// </summary>
    public override int GetHashCode() => Hash(_bytes.Span);

    /// <summary>
    /// The hash <see cref="GetHashCode"/> uses, over any bytes: XxHash32 of the bytes with seed 0,
    /// cast to <see cref="int"/>. Unlike <see cref="HashCode"/> and <see cref="string.GetHashCode()"/>,
    /// which are randomised per process, it is the same everywhere for the same bytes, so a host can
    /// compute it over its own UTF-8 and meet a <see cref="Utf8String"/> in a dictionary or a
    /// partition scheme.
    /// </summary>
    public static int Hash(ReadOnlySpan<byte> bytes) => (int)XxHash32.HashToUInt32(bytes);

    /// <summary>Ordinal byte equality: the same values, byte for byte, wherever the bytes live.</summary>
    public bool Equals(Utf8String other) => AsSpan().SequenceEqual(other._bytes.Span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ReadOnlySpan<byte> other) => AsSpan().SequenceEqual(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ImmutableArray<byte> other) => Equals(other.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(byte[] other) => AsSpan().SequenceEqual(other.AsSpan());

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        ReadOnlySpan<byte> other;
        if (obj is null)
            goto Unsupported;
        else if (obj is Utf8String u8Str)
        {
            if (!u8Str.IsEmpty)
            {
                other = u8Str.AsSpan();
            }
            else goto Empty;
        }
        else if (obj is byte[])
            other = Unsafe.As<byte[]>(obj);
        else if (obj is ImmutableArray<byte>)
            other = Unsafe.Unbox<ImmutableArray<byte>>(obj).AsSpan();
        else goto Unsupported;

        return Equals(other);

        Empty:
        return IsEmpty;

        Unsupported:
        return false;
    }

    /// <summary>
    /// Determines whether this <see cref="Utf8String"/> instance and <paramref name="other"/> are equal
    /// using specified <paramref name="comparer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals<T>(Utf8String other, T comparer)
        where T : IEqualityComparer<Utf8String>
    {
        return comparer.Equals(this, other);
    }

    /// <summary>
    /// Ordinal byte order — Arrow's order, and Chalk's binary collation. A shorter value that is a
    /// prefix of a longer one sorts first.
    /// </summary>
    public int CompareTo(Utf8String other) => AsSpan().SequenceCompareTo(other._bytes.Span);

    /// <summary>Ordinal byte equality.</summary>
    public static bool operator ==(Utf8String left, Utf8String right) => left.Equals(right);

    /// <summary>Ordinal byte inequality.</summary>
    public static bool operator !=(Utf8String left, Utf8String right) => !left.Equals(right);

    /// <summary>Ordinal byte order.</summary>
    public static bool operator <(Utf8String left, Utf8String right) => left.CompareTo(right) < 0;

    /// <summary>Ordinal byte order.</summary>
    public static bool operator <=(Utf8String left, Utf8String right) => left.CompareTo(right) <= 0;

    /// <summary>Ordinal byte order.</summary>
    public static bool operator >(Utf8String left, Utf8String right) => left.CompareTo(right) > 0;

    /// <summary>Ordinal byte order.</summary>
    public static bool operator >=(Utf8String left, Utf8String right) => left.CompareTo(right) >= 0;

    public static bool operator ==(Utf8String left, string? right) => left.Equals(right);

    public static bool operator !=(Utf8String left, string? right) => !left.Equals(right);

    public static bool operator ==(string? left, Utf8String right) => right.Equals(left);

    public static bool operator !=(string? left, Utf8String right) => !right.Equals(left);

    public static bool operator ==(Utf8String left, ReadOnlySpan<byte> right) => left.Equals(right);

    public static bool operator !=(Utf8String left, ReadOnlySpan<byte> right) => !left.Equals(right);

    public static bool operator ==(ReadOnlySpan<byte> left, Utf8String right) => right.Equals(left);

    public static bool operator !=(ReadOnlySpan<byte> left, Utf8String right) => !right.Equals(left);

    /// <summary>
    /// Whether this value spells <paramref name="other"/>: the string transcoded into a stack
    /// buffer and compared byte for byte, allocating nothing. A null string equals nothing, and so
    /// does a string that is not valid UTF-16.
    /// </summary>
    public bool TextEquals(string? other) => Utf8Ordinal.Equals(AsSpan(), other);
}

public static class Utf8StringExtensions
{
    /// <summary>
    /// Whether a run of UTF-8 bytes — one cell of a STRING column read through <c>GetUtf8</c>, say —
    /// spells <paramref name="text"/>, without making a string of either. See
    /// <see cref="Utf8String.TextEquals"/>.
    /// </summary>
    public static bool TextEquals(this ReadOnlySpan<byte> utf8, string? text) => Utf8Ordinal.Equals(utf8, text);

    /// <summary>
    /// <see cref="Utf8String.Hash"/> of a run of UTF-8 bytes: the hash a <see cref="Utf8String"/> of
    /// the same bytes has, and the one <see cref="Utf8StringComparer"/> uses for every spelling.
    /// </summary>
    public static int Utf8Hash(this ReadOnlySpan<byte> utf8) => Utf8String.Hash(utf8);

    public static Utf8String ToUtf8String(this ReadOnlySpan<byte> value)
    {
        if (!Utf8String.IsValidUtf8(value)) throw new ArgumentException("Input is not valid UTF-8.", nameof(value));
        return new Utf8String(value.ToArray());
    }

    public static Utf8String ToUtf8String(this byte[]? value)
    {
        if (!Utf8String.IsValidUtf8(value)) throw new ArgumentException("Input is not valid UTF-8.", nameof(value));
        return new Utf8String(value.AsMemory());
    }

    public static Utf8String ToUtf8String(this ReadOnlyMemory<byte> value)
    {
        if (!Utf8String.IsValidUtf8(value.Span))
            throw new ArgumentException("Input is not valid UTF-8.", nameof(value));
        return new Utf8String(value);
    }

    public static Utf8String ToUtf8String(this object? value) =>
        value switch
        {
            Utf8String utf8 => utf8,
            string text => Utf8String.FromString(text),

            _ => throw new InvalidCastException(
                $"Cannot use {value?.GetType().Name ?? "NULL"} "
                + "as an Akade Utf8String index bound.")
        };

    public static bool TryGetUtf8String(ReadOnlySpan<byte> value, [NotNullWhen(true)] out Utf8String text)
    {
        if (!Utf8String.IsValidUtf8(value))
        {
            text = null;
            return false;
        }

        text = new Utf8String(value.ToArray());
        return true;
    }
    
    public static bool TryGetUtf8String(byte[]? value, [NotNullWhen(true)] out Utf8String text)
    {
        if (!Utf8String.IsValidUtf8(value))
        {
            text = null;
            return false;
        }

        text = new Utf8String(value.AsMemory());
        return true;
    }

    public static bool TryGetUtf8String(ReadOnlyMemory<byte> value, out Utf8String text)
    {
        if (!Utf8String.IsValidUtf8(value.Span))
        {
            text = null;
            return false;
        }

        text = new Utf8String(value);
        return true;
    }
}