using System.Globalization;
using System.Text;
using Chalk.Ir;

namespace Chalk.Execution.Operators;

/// <summary>
/// Writes a pushed <c>LIMIT</c> or <c>OFFSET</c> into the query text a source is sent (D288).
/// </summary>
/// <remarks>
/// <para>
/// A bound that is a parameter travels to the source as its own <c>?</c>, like any other
/// placeholder — and is the one placeholder the provider never binds. When the execution starts the
/// value is read from its slot, refused if it is not a count, and written into the text in that
/// placeholder's place; the entry is then dropped from the list the provider binds, so the
/// remaining placeholders and the remaining values still line up by position.
/// </para>
/// <para>
/// The positions are positions <em>in the text</em>, counted over its <c>?</c>s, which is why the
/// same code serves a dialect that spells the bound <c>LIMIT ?</c> at the end of the statement and
/// one that spells it <c>TOP (?)</c> at the front.
/// </para>
/// <para>
/// A scanner and not a string replace, for the reason the client's own substitution is one: a
/// <c>?</c> inside a quoted identifier, a string literal or a comment is text and not a
/// placeholder, and rewriting one would change which rows the query returns — quietly. The rules
/// are the same rules, so the two never disagree about where a placeholder is.
/// </para>
/// </remarks>
internal static class RenderedBounds
{
    /// <summary>
    /// <paramref name="text"/> with the placeholders at <paramref name="positions"/> replaced by
    /// <paramref name="values"/>.
    /// </summary>
    /// <param name="text">The query text as the planner generated it.</param>
    /// <param name="positions">Placeholder positions, distinct and ascending.</param>
    /// <param name="values">The number for each, in the same order.</param>
    public static string Substitute(
        string text, IReadOnlyList<int> positions, IReadOnlyList<long> values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(values);

        if (positions.Count == 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length + (positions.Count * 20));
        var placeholder = 0;
        var next = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '\'':
                case '"':
                case '`':
                {
                    var end = SkipQuoted(text, i, c);
                    result.Append(text, i, end - i);
                    i = end;
                    continue;
                }

                case '/' when i + 1 < text.Length && text[i + 1] == '*':
                {
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    var end = close < 0 ? text.Length : close + 2;
                    result.Append(text, i, end - i);
                    i = end;
                    continue;
                }

                case '-' when i + 1 < text.Length && text[i + 1] == '-':
                {
                    var newline = text.IndexOf('\n', i);
                    var end = newline < 0 ? text.Length : newline + 1;
                    result.Append(text, i, end - i);
                    i = end;
                    continue;
                }

                case '?':
                    if (next < positions.Count && positions[next] == placeholder)
                    {
                        result.Append(values[next].ToString(CultureInfo.InvariantCulture));
                        next++;
                    }
                    else
                    {
                        result.Append('?');
                    }

                    placeholder++;
                    i++;
                    continue;

                default:
                    result.Append(c);
                    i++;
                    continue;
            }
        }

        if (next != positions.Count)
        {
            throw new InvalidPlanException(
                "I-IR-4",
                "rendered_bounds",
                $"the plan names placeholder {positions[next]} as a bound to render and the query "
                + $"text has {placeholder} placeholder(s)");
        }

        return result.ToString();
    }

    private static int SkipQuoted(string text, int start, char quote)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == quote)
            {
                // A doubled quote is an escape, not the end.
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }
}
