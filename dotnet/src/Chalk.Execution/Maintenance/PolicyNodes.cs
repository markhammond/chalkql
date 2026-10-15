using Chalk.Ir;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// What a maintained plan keeps of a statement's plan under an access policy (D361,
/// <c>docs/design/74-views-under-a-policy.md</c> §2): every filter and every projection the plan puts
/// over a read of a table under a policy, with its own condition and its own expressions. The
/// planner's taint check proves the statement's plan; this holds the maintained plan to it, so that no
/// rewrite drops a row filter or a sanitiser silently.
/// </summary>
/// <remarks>
/// <para>
/// The plan does not say which of those nodes the policy put there and which the query did, so every
/// one of them is held. A row filter is a filter, and a sanitiser, a mask or a group-size floor is a
/// projection. The joins along a path are kept as joins, or the maintained plan would not compile.
/// </para>
/// <para>
/// A node is kept when the maintained plan compiles a filter with its very condition, or a projection
/// with its very expressions. The rewrite carries them rather than copying them, so the check is by
/// reference: an equal condition elsewhere in the plan does not stand in for a missing one.
/// </para>
/// </remarks>
internal static class PolicyNodes
{
    /// <summary>
    /// Refuses a maintained plan that does not keep a filter or a projection the statement's plan puts
    /// over a governed read, naming the first.
    /// </summary>
    /// <param name="plan">The statement's plan.</param>
    /// <param name="governed">
    /// The table a read or a lookup reads under a policy, or as the policy's own read; null for any
    /// other node.
    /// </param>
    /// <param name="compiled">Every tree the maintained plan compiled.</param>
    public static void Check(Plan plan, Func<Rel, string?> governed, IEnumerable<Rel> compiled)
    {
        var held = Held(plan, governed);
        if (held.Count == 0)
        {
            return;
        }

        var conditions = new HashSet<Expr>(ReferenceEqualityComparer.Instance);
        var expressions = new HashSet<Expr>(ReferenceEqualityComparer.Instance);
        foreach (var rel in compiled.SelectMany(PlanWalker.Rels))
        {
            switch (rel.KindCase)
            {
                case Rel.KindOneofCase.Filter:
                    conditions.Add(rel.Filter.Condition);
                    break;
                case Rel.KindOneofCase.Project:
                    expressions.UnionWith(rel.Project.Exprs);
                    break;
            }
        }

        foreach (var (rel, path, table) in held)
        {
            var (kept, what) = rel.KindCase == Rel.KindOneofCase.Filter
                ? (conditions.Contains(rel.Filter.Condition), "filter")
                : (rel.Project.Exprs.All(expressions.Contains), "projection");
            if (!kept)
            {
                throw new NotMaintainedException(
                    $"a {what} over the table '{table}', which carries an access policy, would not be kept as the "
                    + "statement's plan has it.")
                {
                    Path = path,
                };
            }
        }
    }

    /// <summary>Every filter and projection over a governed read, where the plan has it, and the table it is over.</summary>
    private static List<(Rel Rel, string Path, string Table)> Held(Plan plan, Func<Rel, string?> governed)
    {
        var shared = plan.Shared.Where(s => s.Rel is not null).ToDictionary(s => s.Id, s => s.Rel);
        var beneath = new Dictionary<Rel, string?>(ReferenceEqualityComparer.Instance);
        var held = new List<(Rel, string, string)>();

        // The first governed table at or under a node, each subtree walked once.
        string? Visit(Rel rel, string parent)
        {
            if (beneath.TryGetValue(rel, out var known))
            {
                return known;
            }

            var path = $"{parent}/{rel.KindCase}";
            var table = governed(rel);
            if (rel.KindCase == Rel.KindOneofCase.SharedRef && shared.TryGetValue(rel.SharedRef.Id, out var body))
            {
                var named = Visit(body, $"shared#{rel.SharedRef.Id}");
                table ??= named;
            }

            foreach (var input in PlanWalker.Inputs(rel))
            {
                var under = Visit(input, path);
                table ??= under;
            }

            if (table is not null && rel.KindCase is Rel.KindOneofCase.Filter or Rel.KindOneofCase.Project)
            {
                held.Add((rel, path, table));
            }

            beneath[rel] = table;
            return table;
        }

        Visit(plan.Root, "root");
        foreach (var (id, body) in shared)
        {
            Visit(body, $"shared#{id}");
        }

        return held;
    }
}
