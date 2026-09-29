namespace Chalk.Sources;

/// <summary>
/// What a <c>LIKE</c> pattern and its <c>ESCAPE</c> mean, and when they are malformed (D312, D313).
/// </summary>
/// <remarks>
/// <para>
/// The rules are the ones Calcite's own runtime applies, which are the SQL standard's: an escape is
/// exactly one character, and in the pattern it must be followed by <c>%</c>, <c>_</c> or itself.
/// A <c>LIKE</c> with no <c>ESCAPE</c> clause has no escape character at all, so every pattern it is
/// given is well formed and a backslash is an ordinary character. The planner applies the same rules
/// to a literal pattern; these apply them to a parameter's value when it is bound, which is the first
/// moment it exists, and to anything that reads a prefix range.
/// </para>
/// <para>
/// A character is a code point; an unpaired surrogate counts as one, as it does in
/// <see cref="IndexPrefix.TryNext"/>. Positions are 1-based and count characters, and no message
/// quotes the pattern: a pattern is the statement's own data, and a refusal is a log line.
/// </para>
/// </remarks>
public static class LikePattern
{
    /// <summary>
    /// Why <paramref name="escape"/> cannot be an escape, or null when it can. Null means the
    /// <c>LIKE</c> had no <c>ESCAPE</c> clause.
    /// </summary>
    public static string? EscapeDefect(string? escape)
    {
        if (escape is null)
        {
            return null;
        }

        var characters = 0;
        for (var i = 0; i < escape.Length; i += Width(escape, i))
        {
            characters++;
        }

        return characters switch
        {
            1 => null,
            0 => "a LIKE ESCAPE must be exactly one character, not the empty string",
            _ => $"a LIKE ESCAPE must be exactly one character, not {characters} characters",
        };
    }

    /// <summary>
    /// Why <paramref name="pattern"/> is malformed under <paramref name="escape"/>, or null when it is
    /// well formed. The escape is assumed to have passed <see cref="EscapeDefect"/>.
    /// </summary>
    public static string? PatternDefect(string pattern, string? escape)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (escape is null)
        {
            return null;
        }

        var position = 0;
        for (var i = 0; i < pattern.Length;)
        {
            var width = Width(pattern, i);
            var isEscape = IsAt(pattern, i, width, escape);
            i += width;
            position++;
            if (!isEscape)
            {
                continue;
            }

            if (i >= pattern.Length)
            {
                return $"a LIKE pattern ends with its escape character '{escape}' at position "
                    + $"{position}; an escape must be followed by '%', '_' or itself";
            }

            var next = Width(pattern, i);
            if (!(next == 1 && pattern[i] is '%' or '_') && !IsAt(pattern, i, next, escape))
            {
                return $"a LIKE pattern has its escape character '{escape}' before an ordinary "
                    + $"character at position {position}; an escape must be followed by '%', '_' "
                    + "or itself";
            }

            i += next;
            position++;
        }

        return null;
    }

    /// <summary>
    /// Throws an <see cref="ArgumentException"/> when <paramref name="escape"/> or
    /// <paramref name="pattern"/> is malformed; see <see cref="PatternDefect"/>.
    /// </summary>
    public static void Validate(string pattern, string? escape)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var defect = EscapeDefect(escape) ?? PatternDefect(pattern, escape);
        if (defect is not null)
        {
            throw new ArgumentException(defect, nameof(pattern));
        }
    }

    /// <summary>
    /// The literal start of a well-formed pattern — its text up to the first wildcard the escape does
    /// not quote, with the escapes removed — and whether the pattern is exactly that start followed
    /// by one <c>%</c>, which is the only shape a range answers on its own.
    /// </summary>
    /// <remarks>
    /// A pattern with no wildcard at all is an equality: its literal start is the whole text and it
    /// is not a bare prefix, because a prefix range covers it without deciding it.
    /// </remarks>
    public static string LiteralStart(string pattern, string? escape, out bool barePrefix)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var text = new System.Text.StringBuilder(pattern.Length);
        for (var i = 0; i < pattern.Length;)
        {
            var width = Width(pattern, i);
            if (escape is not null && IsAt(pattern, i, width, escape) && i + width < pattern.Length)
            {
                i += width;
                var quoted = Width(pattern, i);
                text.Append(pattern, i, quoted);
                i += quoted;
                continue;
            }

            if (width == 1 && pattern[i] is '%' or '_')
            {
                barePrefix = pattern[i] == '%' && i + 1 == pattern.Length;
                return text.ToString();
            }

            text.Append(pattern, i, width);
            i += width;
        }

        barePrefix = false;
        return text.ToString();
    }

    /// <summary>One character's width in UTF-16 units: two for a surrogate pair, one otherwise.</summary>
    private static int Width(string text, int at) =>
        at + 1 < text.Length && char.IsHighSurrogate(text[at]) && char.IsLowSurrogate(text[at + 1])
            ? 2
            : 1;

    private static bool IsAt(string text, int at, int width, string escape) =>
        width == escape.Length && string.CompareOrdinal(text, at, escape, 0, width) == 0;
}
