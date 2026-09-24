using System.Text;
using Apache.Arrow;

namespace Chalk.Catalog.Tests;

/// <summary>
/// <c>Utf8String</c> semantics (D144, <c>docs/design/24-zero-gc.md</c> §2 and §9): ordinal byte
/// order against Arrow's own, hashing, equality across different memory, the conversions, and the
/// one place a string is made.
/// </summary>
public sealed class Utf8StringTests
{
    [Fact]
    public void Equality_is_over_bytes_wherever_they_live()
    {
        var fromArray = (Utf8String)"café"u8.ToArray();
        var fromMemory = (Utf8String)new ReadOnlyMemory<byte>([..(byte[])[0xFF], .. "café"u8]).Slice(1);
        var fromString = Utf8String.FromString("café");

        Assert.Equal(fromArray, fromMemory);
        Assert.Equal(fromArray, fromString);
        Assert.True(fromArray == fromMemory);
        Assert.False(fromArray != fromString);
        Assert.Equal(fromArray.GetHashCode(), fromMemory.GetHashCode());
        Assert.Equal(fromArray.GetHashCode(), fromString.GetHashCode());
        Assert.True(fromArray.Equals((object)fromMemory));
        Assert.False(fromArray.Equals("café"));
    }

    [Fact]
    public void Length_is_bytes_not_characters()
    {
        var value = Utf8String.FromString("café");
        Assert.Equal(5, value.Length);
        Assert.Equal(4, value.ToString().Length);
        Assert.False(value.IsEmpty);
        Assert.True(Utf8String.Empty.IsEmpty);
        Assert.Equal(0, Utf8String.Empty.Length);
        Assert.Equal(string.Empty, Utf8String.Empty.ToString());
    }

    [Fact]
    public void ToString_is_the_one_materialisation_and_ToArray_copies()
    {
        var bytes = "hello"u8.ToArray();
        var value = (Utf8String)bytes;

        Assert.Equal("hello", value.ToString());

        var copy = value.ToArray();
        Assert.Equal(bytes, copy);
        bytes[0] = (byte)'j';
        Assert.Equal("hello", Encoding.UTF8.GetString(copy));
        Assert.Equal("jello", value.ToString());
    }

    [Fact]
    public void Copy_outlives_the_span_it_came_from()
    {
        Span<byte> scratch = stackalloc byte[5];
        "hello"u8.CopyTo(scratch);
        var copied = Utf8String.Copy(scratch);
        scratch.Clear();

        Assert.Equal("hello", copied.ToString());
        Assert.True(Utf8String.Copy([]).IsEmpty);
    }

    [Fact]
    public void Implicit_conversions_borrow_and_a_null_array_is_empty()
    {
        byte[]? none = null;
        Utf8String fromNull = none;
        Assert.True(fromNull.IsEmpty);
        Assert.True(Utf8String.FromBytes(none).IsEmpty);

        var borrowed = new byte[] { 0x61, 0x62 };
        Utf8String value = borrowed;
        Assert.True(value.AsSpan().SequenceEqual(borrowed));
        Assert.Equal(2, Utf8String.FromBytes(new ReadOnlyMemory<byte>(borrowed)).Length);
        Assert.True(Utf8String.FromString(null).IsEmpty);
        Assert.True(Utf8String.FromString(string.Empty).IsEmpty);
    }

    /// <summary>
    /// The order Chalk's binary collation and Arrow both use, checked against Arrow rather than
    /// asserted: a value sorted by <c>CompareTo</c> must sort the same way a <c>StringArray</c>'s
    /// bytes do.
    /// </summary>
    [Fact]
    public void Order_is_ordinal_and_agrees_with_Arrows_bytes()
    {
        string[] words = ["", "A", "Z", "a", "abc", "ab", "z", "é", "日", "\U0001F600"];

        var builder = new StringArray.Builder();
        foreach (var word in words)
        {
            builder.Append(word);
        }

        using var array = builder.Build();

        var byCompareTo = words.OrderBy(Utf8String.FromString, Comparer<Utf8String>.Default).ToArray();
        var byArrowBytes = Enumerable.Range(0, array.Length)
            .OrderBy(i => Utf8String.Copy(array.GetBytes(i)), Comparer<Utf8String>.Default)
            .Select(i => array.GetString(i))
            .ToArray();

        Assert.Equal(byCompareTo, byArrowBytes);

        // ASCII: uppercase before lowercase, and a prefix before what extends it. Ordinal, not
        // culture — which is exactly what the doc comment promises.
        Assert.True(Utf8String.FromString("Z") < Utf8String.FromString("a"));
        Assert.True(Utf8String.FromString("ab") < Utf8String.FromString("abc"));
        Assert.True(Utf8String.FromString("abc") > Utf8String.FromString("ab"));
        Assert.True(Utf8String.FromString("a") <= Utf8String.FromString("a"));
        Assert.True(Utf8String.FromString("a") >= Utf8String.FromString("a"));
        Assert.Equal(0, Utf8String.FromString("a").CompareTo(Utf8String.FromString("a")));
    }

    [Fact]
    public void GetUtf8_lends_an_Arrow_arrays_own_bytes_and_a_null_row_is_default()
    {
        var builder = new StringArray.Builder();
        builder.Append("café");
        builder.AppendNull();
        builder.Append(string.Empty);
        using var array = builder.Build();

        // A batch hands out bytes, never a borrowed Utf8String: the span is read in place, and a
        // value to keep is copied out of it.
        Assert.Equal(Utf8String.FromString("café"), Utf8String.Copy(array.GetBytes(0)));
        Assert.Equal("café", Encoding.UTF8.GetString(array.GetBytes(0)));
        Assert.True(array.GetBytes(1).IsEmpty);
        Assert.True(array.GetBytes(2).IsEmpty);
    }

    /// <summary>
    /// The trap the implicit conversions buy, pinned so it stays documented rather than discovered:
    /// a <c>null</c> literal beside a <see cref="Utf8String"/> binds to the <c>byte[]</c> conversion,
    /// so a conditional never produces a null <c>Utf8String?</c>. Write the NULL out.
    /// </summary>
    [Fact]
    public void A_null_literal_in_a_conditional_becomes_the_empty_value_not_a_null()
    {
        var flag = false;
        var trap = flag ? Utf8String.FromString("x") : null;
        Assert.IsType<Utf8String>(trap);
        Assert.True(trap.IsEmpty);

        Utf8String? written = default;
        if (flag)
        {
            written = Utf8String.FromString("x");
        }

        Assert.False(written.HasValue);
    }

    /// <summary>
    /// Validation is a question the caller asks, not something construction does: wrapping bytes is
    /// free, and the check happens where bytes enter a column.
    /// </summary>
    [Fact]
    public void Validation_is_available_but_never_automatic()
    {
        var invalid = (Utf8String)new byte[] { 0xC3, 0x28 };
        Assert.False(invalid.IsValid());
        Assert.False(Utf8String.IsValidUtf8([0xFF]));
        Assert.True(Utf8String.FromString("café").IsValid());
        Assert.True(Utf8String.Empty.IsValid());
        Assert.True(Utf8String.IsValidUtf8([]));
    }

    [Fact]
    public void The_hash_is_deterministic_XxHash32_and_a_host_can_compute_it()
    {
        // XxHash32 with seed 0, cast to int (D144): the same bytes hash the same in every process.
        Assert.Equal(852579327, Utf8String.Hash("abc"u8));
        Assert.Equal(46947589, Utf8String.Hash([]));
        Assert.Equal(-12863435, Utf8String.Hash("BTCUSDT"u8));
        Assert.Equal(Utf8String.Hash("abc"u8), ((Utf8String)"abc"u8.ToArray()).GetHashCode());
        Assert.Equal(Utf8String.Hash("abc"u8), Utf8String.FromString("abc").GetHashCode());
        Assert.Equal(46947589, Utf8String.Empty.GetHashCode());
    }
}
