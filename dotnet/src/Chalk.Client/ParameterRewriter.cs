using System.Globalization;
using System.Text;

namespace Chalk.Client;

/// <summary>Which parameter syntax a statement uses. Styles never mix within one statement (D27).</summary>
public enum ParameterStyle
{
    /// <summary>No parameters.</summary>
    None,

    /// <summary><c>?</c> — each occurrence is its own parameter.</summary>
    Positional,

    /// <summary><c>$1 $2 …</c> — 1-based, an ordinal may recur.</summary>
    Ordinal,

    /// <summary><c>@name</c> — the .NET convention; case-insensitive, may recur.</summary>
    Named,
}

/// <summary>
/// One logical parameter after the rewrite. A <c>$1</c> or <c>@name</c> that appears three times is
/// one descriptor with three occurrences, bound once.
/// </summary>
public sealed class ParameterDescriptor
{
    /// <summary>The name, for the <c>@name</c> style; null otherwise.</summary>
    public string? Name { get; init; }

    /// <summary>1-based position: the ordinal for <c>$n</c>, otherwise the order of first appearance.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Indexes into the rewritten statement's placeholder list.</summary>
    public required IReadOnlyList<int> Occurrences { get; init; }

    /// <summary>
    /// True when every occurrence directly follows <c>IN</c> or <c>NOT IN</c>, so a list may be bound
    /// to it and expanded (D29). Binding a list anywhere else is an error naming the parameter.
    /// </summary>
    public required bool AcceptsList { get; init; }

    /// <summary>
    /// The type the planner inferred for this parameter — the element type when
    /// <see cref="AcceptsList"/>. Set once the statement has been planned.
    /// </summary>
    public Chalk.Catalog.ChalkType Type { get; internal set; }

    /// <summary>The parameter as the statement writes it, so an error message can be searched for.</summary>
    public override string ToString() =>
        Name is null ? "$" + Ordinal.ToString(CultureInfo.InvariantCulture) : "@" + Name;
}

/// <summary>Which value a rendered <c>?</c> takes.</summary>
/// <param name="ParameterIndex">Index into the statement's logical parameters.</param>
/// <param name="ElementIndex">
/// The element of a bound list, or <see cref="Scalar"/> for a scalar, or <see cref="EmptyListNull"/>
/// for the NULL placeholder that stands in for an empty <c>IN</c> list.
/// </param>
internal readonly record struct PlaceholderSlot(int ParameterIndex, int ElementIndex)
{
    public const int Scalar = -1;
    public const int EmptyListNull = -2;
}

/// <summary>The SQL to send, and what each of its <c>?</c> placeholders takes.</summary>
internal sealed record RenderedStatement(string Sql, IReadOnlyList<PlaceholderSlot> Slots);

/// <summary>
/// Calcite's parser knows only positional <c>?</c> (V13), so the three syntaxes a host may write are
/// a client-side rewrite (D27), and a list bound to an <c>IN</c> parameter is expanded here rather
/// than in the planner (D29). A pure function over the statement text: no catalog, no planner.
///
/// <para>
/// It tokenizes just enough SQL to know what is *not* a parameter — <c>'…'</c> string literals with
/// <c>''</c> escapes, <c>"…"</c> quoted identifiers, <c>--</c> line comments and <c>/* */</c> block
/// comments — and scans everything else for <c>?</c>, <c>$n</c> and <c>@name</c>.
/// </para>
/// </summary>
internal sealed class ParameterRewriter
{
    private readonly List<Segment> _segments = [];
    private readonly List<Occurrence> _occurrences = [];

    private ParameterRewriter(ParameterStyle style, IReadOnlyList<ParameterDescriptor> parameters)
    {
        Style = style;
        Parameters = parameters;
    }

    public ParameterStyle Style { get; }

    public IReadOnlyList<ParameterDescriptor> Parameters { get; }

    /// <summary>How many <c>?</c> the planner sees in the one-element shape.</summary>
    public int OccurrenceCount => _occurrences.Count;

    /// <summary>Parses a statement and returns its rewriter, or throws naming the problem.</summary>
    public static ParameterRewriter Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var scanner = new Scanner(sql);
        scanner.Run();

        var style = scanner.Style;
        var parameters = scanner.BuildParameters();
        var occurrences = scanner.Occurrences.ToList();

        if (style == ParameterStyle.Ordinal)
        {
            // The scanner allocates parameters in order of first appearance, but a host binding
            // `$n` supplies values in ordinal order: `WHERE a = $2 AND b = $1` takes [b's value,
            // a's value]. Reorder the parameters and remap the occurrences that point at them.
            var order = Enumerable.Range(0, parameters.Count)
                .OrderBy(i => parameters[i].Ordinal)
                .ToArray();
            var moved = new int[parameters.Count];
            for (var position = 0; position < order.Length; position++)
            {
                moved[order[position]] = position;
            }

            occurrences = [.. occurrences.Select(o => o with { ParameterIndex = moved[o.ParameterIndex] })];
            parameters = [.. order.Select(i => parameters[i])];
        }

        var rewriter = new ParameterRewriter(style, parameters);
        rewriter._segments.AddRange(scanner.Segments);
        rewriter._occurrences.AddRange(occurrences);
        return rewriter;
    }

    /// <summary>
    /// The shape <c>PrepareAsync</c> plans: every list-capable parameter has exactly one element, so
    /// validation errors and element types surface at prepare time (§7.4).
    /// </summary>
    public IReadOnlyList<int> PrepareShape() =>
        [.. Parameters.Select(p => p.AcceptsList ? 1 : PlaceholderSlot.Scalar)];

    /// <summary>
    /// Renders the SQL for a shape: the list length per parameter, or <see cref="PlaceholderSlot.Scalar"/>
    /// for a parameter bound to a single value.
    /// </summary>
    public RenderedStatement Render(IReadOnlyList<int> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Count != Parameters.Count)
        {
            throw new ArgumentException(
                $"the shape has {shape.Count} entries but the statement has {Parameters.Count} parameters",
                nameof(shape));
        }

        var sql = new StringBuilder();
        var slots = new List<PlaceholderSlot>();

        foreach (var segment in _segments)
        {
            if (segment.Occurrence < 0)
            {
                sql.Append(segment.Text);
                continue;
            }

            var occurrence = _occurrences[segment.Occurrence];
            var parameter = occurrence.ParameterIndex;
            var length = shape[parameter];

            if (length == PlaceholderSlot.Scalar)
            {
                sql.Append('?');
                slots.Add(new PlaceholderSlot(parameter, PlaceholderSlot.Scalar));
                continue;
            }

            if (length == 0)
            {
                // `x IN (NULL)` and `x NOT IN (NULL)` are both NULL for every x, so the postfix test
                // yields FALSE for IN and TRUE for NOT IN — the SQL results of an empty list —
                // without touching the left operand. IN binds tighter than IS, which binds tighter
                // than NOT/AND/OR, so no parentheses are needed around the whole thing.
                sql.Append("(?) IS ").Append(occurrence.NegatedIn ? "NOT FALSE" : "FALSE");
                slots.Add(new PlaceholderSlot(parameter, PlaceholderSlot.EmptyListNull));
                continue;
            }

            sql.Append('(');
            for (var i = 0; i < length; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                sql.Append('?');
                slots.Add(new PlaceholderSlot(parameter, i));
            }

            sql.Append(')');
        }

        return new RenderedStatement(sql.ToString(), slots);
    }

    /// <summary>
    /// The host's name for each rendered <c>?</c>, in order — <c>@symbol</c> for a parameter the
    /// statement wrote as <c>@symbol</c>, once per occurrence — or null for a statement whose
    /// parameters are not named, which has nothing to put back (D287).
    /// </summary>
    public IReadOnlyList<string>? SlotNames(RenderedStatement rendered)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        if (Style != ParameterStyle.Named)
        {
            return null;
        }

        var names = new string[rendered.Slots.Count];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = "@" + Parameters[rendered.Slots[i].ParameterIndex].Name;
        }

        return names;
    }

    /// <summary>A literal run of SQL, or a parameter occurrence to render.</summary>
    private readonly record struct Segment(string Text, int Occurrence);

    /// <summary>One <c>?</c>, <c>$n</c> or <c>@name</c> in the source text.</summary>
    private readonly record struct Occurrence(int ParameterIndex, bool FollowsIn, bool NegatedIn);

    private sealed class Scanner(string sql)
    {
        private readonly List<Segment> _segments = [];
        private readonly List<Occurrence> _occurrences = [];
        private readonly List<string> _names = [];
        private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> _byOrdinal = [];
        private readonly List<List<int>> _occurrencesByParameter = [];
        private readonly List<bool> _acceptsList = [];
        private readonly List<int> _ordinals = [];

        private string? _firstStyleToken;
        private int _copiedTo;
        private string _previousWord = string.Empty;
        private string _wordBeforeThat = string.Empty;

        public ParameterStyle Style { get; private set; } = ParameterStyle.None;

        public IReadOnlyList<Segment> Segments => _segments;

        public IReadOnlyList<Occurrence> Occurrences => _occurrences;

        public void Run()
        {
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                switch (c)
                {
                    case '\'':
                        i = SkipQuoted(i, '\'');
                        continue;
                    case '"':
                        i = SkipQuoted(i, '"');
                        continue;
                    case '-' when i + 1 < sql.Length && sql[i + 1] == '-':
                        i = SkipLineComment(i);
                        continue;
                    case '/' when i + 1 < sql.Length && sql[i + 1] == '*':
                        i = SkipBlockComment(i);
                        continue;
                    case '?':
                        AddOccurrence(i, i + 1, ParameterStyle.Positional, name: null, ordinal: 0);
                        i++;
                        continue;
                    case '$' when i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1]):
                    {
                        var end = i + 1;
                        while (end < sql.Length && char.IsAsciiDigit(sql[end]))
                        {
                            end++;
                        }

                        var ordinal = int.Parse(sql.AsSpan(i + 1, end - i - 1), CultureInfo.InvariantCulture);
                        AddOccurrence(i, end, ParameterStyle.Ordinal, name: null, ordinal);
                        i = end;
                        continue;
                    }

                    case '@' when i + 1 < sql.Length && IsNameStart(sql[i + 1]):
                    {
                        var end = i + 1;
                        while (end < sql.Length && IsNamePart(sql[end]))
                        {
                            end++;
                        }

                        AddOccurrence(i, end, ParameterStyle.Named, sql[(i + 1)..end], ordinal: 0);
                        i = end;
                        continue;
                    }

                    default:
                        i = ConsumeWord(i);
                        continue;
                }
            }

            Flush(sql.Length);
        }

        /// <summary>The logical parameters, in the order the host will bind them.</summary>
        public IReadOnlyList<ParameterDescriptor> BuildParameters()
        {
            if (Style == ParameterStyle.Ordinal)
            {
                // Gaps are an error: `$1, $3` with no `$2` would silently shift every value.
                var highest = _ordinals.Count == 0 ? 0 : _ordinals.Max();
                for (var ordinal = 1; ordinal <= highest; ordinal++)
                {
                    if (!_byOrdinal.ContainsKey(ordinal))
                    {
                        throw new ArgumentException(
                            $"the statement uses ${highest} but never ${ordinal}; ordinal parameters are "
                            + "1-based and must not skip a number");
                    }
                }
            }

            var parameters = new List<ParameterDescriptor>(_occurrencesByParameter.Count);
            for (var i = 0; i < _occurrencesByParameter.Count; i++)
            {
                parameters.Add(new ParameterDescriptor
                {
                    Name = Style == ParameterStyle.Named ? _names[i] : null,
                    Ordinal = Style == ParameterStyle.Ordinal ? _ordinals[i] : i + 1,
                    Occurrences = _occurrencesByParameter[i],
                    AcceptsList = _acceptsList[i],
                });
            }

            return parameters;
        }

        private void AddOccurrence(int start, int end, ParameterStyle style, string? name, int ordinal)
        {
            RequireStyle(style, sql[start..end]);
            Flush(start);
            _copiedTo = end;

            var parameterIndex = Resolve(style, name, ordinal);
            var followsIn = string.Equals(_previousWord, "IN", StringComparison.OrdinalIgnoreCase);
            var negated = followsIn && string.Equals(_wordBeforeThat, "NOT", StringComparison.OrdinalIgnoreCase);

            // A parameter accepts a list only if *every* occurrence sits in an IN position; one that
            // does not would have to be both a list and a scalar.
            _acceptsList[parameterIndex] = _acceptsList[parameterIndex] && followsIn;

            _occurrencesByParameter[parameterIndex].Add(_occurrences.Count);
            _segments.Add(new Segment(string.Empty, _occurrences.Count));
            _occurrences.Add(new Occurrence(parameterIndex, followsIn, negated));

            _wordBeforeThat = _previousWord;
            _previousWord = "?";
        }

        private int Resolve(ParameterStyle style, string? name, int ordinal)
        {
            switch (style)
            {
                case ParameterStyle.Named:
                    if (_byName.TryGetValue(name!, out var existingByName))
                    {
                        return existingByName;
                    }

                    _byName[name!] = _names.Count;
                    _names.Add(name!);
                    break;

                case ParameterStyle.Ordinal:
                    if (_byOrdinal.TryGetValue(ordinal, out var existingByOrdinal))
                    {
                        return existingByOrdinal;
                    }

                    _byOrdinal[ordinal] = _ordinals.Count;
                    _names.Add(string.Empty);
                    break;

                default:
                    // Positional: each `?` is its own parameter, so there is nothing to look up.
                    _names.Add(string.Empty);
                    break;
            }

            var index = _occurrencesByParameter.Count;
            _occurrencesByParameter.Add([]);
            _acceptsList.Add(true);
            _ordinals.Add(style == ParameterStyle.Ordinal ? ordinal : index + 1);
            return index;
        }

        private void RequireStyle(ParameterStyle style, string token)
        {
            if (Style == ParameterStyle.None)
            {
                Style = style;
                _firstStyleToken = token;
                return;
            }

            if (Style != style)
            {
                throw new ArgumentException(
                    $"the statement mixes parameter styles: '{_firstStyleToken}' is {Style} but '{token}' is "
                    + $"{style}. Use one style per statement (docs/design/04-client.md §7.3).");
            }
        }

        private void Flush(int upTo)
        {
            if (upTo > _copiedTo)
            {
                _segments.Add(new Segment(sql[_copiedTo..upTo], -1));
                _copiedTo = upTo;
            }
        }

        private int SkipQuoted(int start, char quote)
        {
            var i = start + 1;
            while (i < sql.Length)
            {
                if (sql[i] == quote)
                {
                    // A doubled quote is an escape, not the end.
                    if (i + 1 < sql.Length && sql[i + 1] == quote)
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                i++;
            }

            _previousWord = quote == '"' ? "\"id\"" : "'literal'";
            return i;
        }

        private int SkipLineComment(int start)
        {
            var end = sql.IndexOf('\n', start);
            return end < 0 ? sql.Length : end + 1;
        }

        private int SkipBlockComment(int start)
        {
            var end = sql.IndexOf("*/", start + 2, StringComparison.Ordinal);
            return end < 0 ? sql.Length : end + 2;
        }

        /// <summary>
        /// Consumes one word (or one punctuation character), keeping the last two words so an
        /// occurrence knows whether it directly follows <c>IN</c> or <c>NOT IN</c>.
        /// </summary>
        private int ConsumeWord(int start)
        {
            if (IsNameStart(sql[start]))
            {
                var end = start;
                while (end < sql.Length && IsNamePart(sql[end]))
                {
                    end++;
                }

                _wordBeforeThat = _previousWord;
                _previousWord = sql[start..end];
                return end;
            }

            if (!char.IsWhiteSpace(sql[start]))
            {
                _wordBeforeThat = _previousWord;
                _previousWord = sql[start].ToString();
            }

            return start + 1;
        }

        private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';

        private static bool IsNamePart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    }
}
