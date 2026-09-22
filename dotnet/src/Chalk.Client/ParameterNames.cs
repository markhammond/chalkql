using System.Globalization;
using System.Text;

namespace Chalk.Client;

/// <summary>
/// Puts a host's parameter names back into a text the planner rendered with positional placeholders
/// (D287): the redacted statement, where every parameter reads <c>?</c>, and the redacted plan text,
/// where a dynamic parameter reads <c>?0</c>, <c>?1</c>, … — so a logged line names the parameter
/// the host wrote rather than its position.
/// </summary>
/// <remarks>
/// A scanner and not a string replace: a <c>?</c> inside a quoted identifier, a string or a comment
/// — and a pseudonym's marker is a comment — is left alone. For the statement form the substitution
/// is all or nothing: unless the placeholders it finds are exactly as many as the rewrite produced,
/// the text is returned as served, because a <c>?</c> is honest and a wrong name is not.
/// </remarks>
internal static class ParameterNames
{
    /// <summary>
    /// <paramref name="text"/> with its placeholders replaced by <paramref name="names"/>.
    /// </summary>
    /// <param name="text">The text as the planner rendered it.</param>
    /// <param name="names">The host's name for each rendered placeholder, in order.</param>
    /// <param name="indexed">
    /// True for the plan text, whose placeholders carry their index (<c>?3</c> is the fourth); false
    /// for a statement, whose bare <c>?</c>s are taken in order.
    /// </param>
    public static string Substitute(string text, IReadOnlyList<string> names, bool indexed)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);

        var result = new StringBuilder(text.Length + names.Count * 8);
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

                case '?' when indexed:
                {
                    var end = i + 1;
                    while (end < text.Length && char.IsAsciiDigit(text[end]))
                    {
                        end++;
                    }

                    if (end > i + 1
                        && int.TryParse(
                            text.AsSpan(i + 1, end - i - 1),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var index)
                        && index < names.Count)
                    {
                        result.Append(names[index]);
                    }
                    else
                    {
                        result.Append(text, i, end - i);
                    }

                    i = end;
                    continue;
                }

                case '?':
                    if (next >= names.Count)
                    {
                        // More placeholders than the rewrite produced: not a text this can name.
                        return text;
                    }

                    result.Append(names[next++]);
                    i++;
                    continue;

                default:
                    result.Append(c);
                    i++;
                    continue;
            }
        }

        return !indexed && next != names.Count ? text : result.ToString();
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
