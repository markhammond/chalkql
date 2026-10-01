using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution;

/// <summary>
/// The parameters a plan uses as <c>LIKE</c> patterns, each with the escape it is read under — so a
/// value that is malformed under the escape rules (D312) is refused when the statement is executed,
/// before anything runs, wherever the <c>LIKE</c> would have run.
/// </summary>
/// <remarks>
/// <para>
/// Wherever includes a source. A pattern the engine evaluates is checked again where it compiles;
/// one inside a pushed subtree is sent to the database as it is, and PostgreSQL and DuckDB each
/// accept something the rules refuse. Checking the value once, here, against every use the plan
/// makes of it — the pushed plan included — is what makes the refusal the same everywhere.
/// </para>
/// <para>
/// Found once per compiled plan, by walking it; checked once per execution, by reading a list.
/// </para>
/// </remarks>
internal sealed class LikeParameters
{
    private readonly Entry[] _entries;

    private LikeParameters(Entry[] entries) => _entries = entries;

    /// <summary>A plan that uses no parameter as a pattern.</summary>
    public static LikeParameters None { get; } = new([]);

    /// <summary>Every parameter <paramref name="plan"/> uses as a pattern, pushed subtrees included.</summary>
    public static LikeParameters Of(Plan plan, IReadOnlyDictionary<string, int> boundSlots)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var entries = new List<Entry>();
        foreach (var rel in PlanWalker.Rels(plan))
        {
            foreach (var expr in PlanWalker.OwnExprs(rel).SelectMany(PlanWalker.Exprs))
            {
                if (expr.KindCase == Expr.KindOneofCase.Call
                    && expr.Call.Function is FunctionId.Like or FunctionId.Ilike
                    && expr.Call.Args.Count >= 2
                    && expr.Call.Args[1].KindCase == Expr.KindOneofCase.Param)
                {
                    var escape = expr.Call.Args.Count > 2
                        && expr.Call.Args[2].KindCase == Expr.KindOneofCase.Literal
                        && expr.Call.Args[2].Literal.ValueCase == Literal.ValueOneofCase.StringValue
                            ? expr.Call.Args[2].Literal.StringValue
                            : null;
                    Add(entries, expr.Call.Args[1].Param, escape, boundSlots);
                }
            }

            if (rel.KindCase != Rel.KindOneofCase.IndexLookup)
            {
                continue;
            }

            foreach (var range in rel.IndexLookup.Ranges)
            {
                if (range.Prefix
                    && range.Lower.Count > 0
                    && range.Lower[^1].KindCase == Expr.KindOneofCase.Param)
                {
                    Add(
                        entries,
                        range.Lower[^1].Param,
                        range.Escape.Length > 0 ? range.Escape : null,
                        boundSlots);
                }
            }
        }

        return entries.Count == 0 ? None : new LikeParameters([.. entries]);
    }

    /// <summary>
    /// Throws an <see cref="ArgumentException"/> naming the parameter when a value is malformed under
    /// the escape its pattern is read with. A NULL value is not a pattern and not a defect.
    /// </summary>
    public void Validate(IReadOnlyList<object?> values)
    {
        foreach (var entry in _entries)
        {
            if (entry.Slot >= values.Count || IndexPrefix.AsText(values[entry.Slot]) is not { } pattern)
            {
                continue;
            }

            var defect = LikePattern.EscapeDefect(entry.Escape)
                ?? LikePattern.PatternDefect(pattern, entry.Escape);
            if (defect is not null)
            {
                throw new ParameterBindingException(
                    entry.Label,
                    $"The value bound to {entry.Label} is used as a LIKE pattern and is malformed: "
                    + $"{defect}.");
            }
        }
    }

    private static void Add(
        List<Entry> entries, DynamicParam param, string? escape, IReadOnlyDictionary<string, int> slots)
    {
        var slot = BoundSlots.Of(param, slots);
        if (entries.Any(e => e.Slot == slot && e.Escape == escape))
        {
            return;
        }

        var label = param.BoundKey.Length > 0 ? $"context value '{param.BoundKey}'" : $"parameter ?{param.Index}";
        entries.Add(new Entry(slot, escape, label));
    }

    private sealed class Entry(int slot, string? escape, string label)
    {
        public int Slot { get; } = slot;

        public string? Escape { get; } = escape;

        public string Label { get; } = label;
    }
}
