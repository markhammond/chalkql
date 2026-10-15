using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// A key held as a CLR string orders as the same key held as UTF-8: by code point, so a character
/// beyond U+FFFF sorts above U+E000..U+FFFF, where comparing UTF-16 units puts it below.
/// </summary>
public sealed class TextOrderTests
{
    private static readonly string[] Texts =
    [
        "",
        "a",
        "a\uFFFD",
        "a\U00010000",
        "\u00E9",
        "\uD7FF",
        "\uE000",
        "\uFFFD",
        "\U00010000",
        "\U0010FFFF",
    ];

    [Fact]
    public void A_string_orders_by_code_point_as_its_UTF8_does()
    {
        foreach (var left in Texts)
        {
            foreach (var right in Texts)
            {
                var expected = Math.Sign(Utf8String.FromString(left).CompareTo(Utf8String.FromString(right)));
                var comparer = SourceValueOrder.CreateComparer(left, SortDirection.AscNullsLast);

                Assert.Equal(expected, Math.Sign(TextOrder.Compare(left, right)));
                Assert.Equal(expected, Math.Sign(SourceValueOrder.CompareValues(left, right)));
                Assert.Equal(expected, Math.Sign(comparer.Compare(left, right)));
            }
        }
    }

    /// <summary>The case the order is for: U+10000 is the pair D800 DC00, whose first unit is below U+FFFD.</summary>
    [Fact]
    public void A_character_beyond_U_FFFF_sorts_above_the_specials_block()
    {
        Assert.True(string.CompareOrdinal("\U00010000", "\uFFFD") < 0);

        Assert.True(TextOrder.Compare("\U00010000", "\uFFFD") > 0);
        Assert.True(SourceValueOrder.CompareValues("a\U00010000", "a\uFFFD") > 0);
    }
}
