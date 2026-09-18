namespace Chalk.TestKit;

/// <summary>
/// The canaries of <c>docs/design/32-adversarial-entitlements.md</c> §2 (D252): a unique token in
/// every value the tenancy policy can mask or hide, and the amounts that stand for the one numeric
/// column it can.
/// </summary>
/// <remarks>
/// <para>
/// A token sits <b>after</b> whatever prefix a mask discloses — the initial mask discloses one
/// character and the excerpt mask nine — so a legitimately disclosed prefix never contains one, and
/// a token in anything a principal receives is a value that reached them whole. Equal values carry
/// the <em>same</em> token, because the token stands for the value and not for the row: two members
/// share a first name so that §8's query 19 joins on a fingerprint rather than on reflexivity, and
/// a token per row would have made that join assert nothing.
/// </para>
/// <para>
/// The tokens are never enumerated by hand. <see cref="Extract"/> reads them back out of text, so
/// the universe is whatever the fixture's own rows hold and a principal's allowed set is whatever
/// the oracle discloses to them — which is D252's definition rather than a second copy of it.
/// </para>
/// </remarks>
public static class TenancyCanaries
{
    /// <summary>What every token begins with. No fixture value spells it for any other reason.</summary>
    public const string Marker = "CANARY-";

    /// <summary>
    /// The canary <c>orders.amount</c> of each row, in the fixture's own order. Deliberately far
    /// apart and deliberately not an arithmetic progression: a mean of two of them must not land on
    /// a third, or an aggregate a principal is entitled to would read as a value they are not.
    /// </summary>
    public static IReadOnlyList<long> Amounts { get; } =
        [8100013, 8203307, 8311111, 8427761, 8549939, 8677777, 8811119];

    /// <summary>
    /// One value with its token appended. The separator is a space, so the value reads as itself
    /// with a label after it rather than as a different value.
    /// </summary>
    /// <param name="value">The value the fixture would otherwise carry.</param>
    /// <param name="group">The column family the token belongs to, upper case.</param>
    /// <param name="ordinal">Which distinct value of that family this is.</param>
    public static string Mark(string value, string group, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrEmpty(group);
        return $"{value} {Token(group, ordinal)}";
    }

    /// <summary>The token a column family's nth distinct value carries.</summary>
    public static string Token(string group, int ordinal)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);
        return $"{Marker}{group}-{ordinal:00}";
    }

    /// <summary>
    /// Every canary token in <paramref name="text"/>. A token runs from <see cref="Marker"/> to the
    /// last character that can be part of one, which is what lets it be found inside a row rendered
    /// as text, a plan, a report or a message alike.
    /// </summary>
    public static IEnumerable<string> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var at = 0;
        while (at < text.Length)
        {
            var start = text.IndexOf(Marker, at, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            var end = start + Marker.Length;
            while (end < text.Length && IsTokenChar(text[end]))
            {
                end++;
            }

            yield return text[start..end];
            at = end;
        }
    }

    private static bool IsTokenChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-';
}
