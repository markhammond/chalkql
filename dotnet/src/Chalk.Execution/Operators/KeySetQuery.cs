using System.Text;

namespace Chalk.Execution.Operators;

/// <summary>
/// A lookup query whose key set is one placeholder, expanded per call into as many as the call
/// carries (M5, D105, <c>20-m5-federation.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// The planner writes the predicate as <c>"symbol" IN (?)</c> — or <c>"symbol" IN (VALUES (?))</c>
/// for a broadcast — with a single placeholder standing for the whole set, because at planning time
/// nobody knows how many keys a call will have. The executor expands that one placeholder into the
/// number of keys it is about to bind: <c>?, ?, ?</c> for an <c>IN</c> list, <c>(?), (?), (?)</c>
/// for <c>VALUES</c> rows. Which one is on the plan's <c>LookupJoin.key_set_rows</c>, so the
/// executor never has to read the SQL to find out.
/// </para>
/// <para>
/// A key set over several columns (F50) is written <c>("member_id", "org_id") IN (?)</c> and
/// expands the same way, a <em>row</em> at a time: <c>(?, ?), (?, ?), (?, ?)</c>. The column count
/// is how many key pairs the <c>LookupJoin</c> carries, so this class is told it rather than
/// reading the SQL for it either.
/// </para>
/// <para>
/// Finding the placeholder is a scan, not a search: a <c>?</c> inside a string literal or a quoted
/// identifier is data, and the same care <c>AdoParameters.Rewrite</c> takes on the adapter's side is
/// taken here. The planner refuses to emit a lookup whose subtree carries any other dynamic
/// parameter, so in practice the key set is the only <c>?</c> there is — but the ordinal is honoured
/// anyway, because a rule that only works by accident is one nobody can change.
/// </para>
/// <para>
/// Texts are cached by key count. A lookup makes many calls of exactly <c>max_keys_per_call</c> keys
/// and at most one short final one, so the cache holds two entries for a long join and one for a
/// short one — which is also what lets a driver's own statement cache do its job.
/// </para>
/// </remarks>
internal sealed class KeySetQuery
{
    private readonly string _text;
    private readonly int _ordinal;
    private readonly bool _asRows;
    private readonly int _columns;
    private readonly Dictionary<int, string> _byCount = [];

    /// <param name="text">The generated SQL, with one placeholder standing for the key set.</param>
    /// <param name="ordinal">Which <c>?</c> in <paramref name="text"/> the key set is, zero-based.</param>
    /// <param name="asRows">Expand as <c>VALUES</c> rows rather than as an <c>IN</c> list.</param>
    /// <param name="columns">How many columns one key has (F50). One unless the key is composite.</param>
    public KeySetQuery(string text, int ordinal, bool asRows, int columns = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        _text = text;
        _ordinal = ordinal;
        _asRows = asRows;
        _columns = columns;
    }

    /// <summary>The query text for a call carrying <paramref name="keys"/> keys.</summary>
    public string For(int keys)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keys);
        if (_byCount.TryGetValue(keys, out var cached))
        {
            return cached;
        }

        var expanded = Expand(_text, _ordinal, keys, _asRows, _columns);
        _byCount[keys] = expanded;
        return expanded;
    }

    /// <summary>
    /// The text with the key-set placeholder replaced by <paramref name="keys"/> keys of
    /// <paramref name="columns"/> columns each. Public for the test that asserts the spellings
    /// directly.
    /// </summary>
    internal static string Expand(string text, int ordinal, int keys, bool asRows, int columns = 1)
    {
        var at = PlaceholderAt(text, ordinal);
        if (at < 0)
        {
            throw new InvalidOperationException(
                $"the lookup query has no placeholder #{ordinal} to bind a key set into: {text}");
        }

        // The separator already supplies the brackets a VALUES row needs, because the planner wrote
        // the first row's pair; a multi-column IN list has to bracket each key row itself.
        var separator = asRows ? "), (" : ", ";
        var bracket = columns > 1 && !asRows;
        var replacement = new StringBuilder(keys * columns * 3);
        for (var i = 0; i < keys; i++)
        {
            if (i > 0)
            {
                replacement.Append(separator);
            }

            if (bracket)
            {
                replacement.Append('(');
            }

            for (var c = 0; c < columns; c++)
            {
                if (c > 0)
                {
                    replacement.Append(", ");
                }

                replacement.Append('?');
            }

            if (bracket)
            {
                replacement.Append(')');
            }
        }

        return string.Concat(text.AsSpan(0, at), replacement.ToString(), text.AsSpan(at + 1));
    }

    /// <summary>
    /// The index of the <paramref name="ordinal"/>-th top-level <c>?</c>, or -1. Quote-aware: a
    /// <c>?</c> inside <c>'…'</c> or <c>"…"</c> is a character of a value or a name, not a
    /// placeholder, and doubled quotes escape themselves in both.
    /// </summary>
    private static int PlaceholderAt(string text, int ordinal)
    {
        var seen = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(text, i, c);
                continue;
            }

            if (c == '?')
            {
                if (seen == ordinal)
                {
                    return i;
                }

                seen++;
            }

            i++;
        }

        return -1;
    }

    /// <summary>The index just past a quoted run starting at <paramref name="start"/>.</summary>
    private static int SkipQuoted(string text, int start, char quote)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] != quote)
            {
                i++;
                continue;
            }

            // A doubled quote is one quote character, not the end of the run.
            if (i + 1 < text.Length && text[i + 1] == quote)
            {
                i += 2;
                continue;
            }

            return i + 1;
        }

        return i;
    }
}
