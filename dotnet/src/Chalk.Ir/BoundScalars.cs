namespace Chalk.Ir;

/// <summary>
/// The context scalars a plan reads at execution, in one canonical order
/// (<c>docs/design/16-entitlements.md</c> §2, D209).
/// </summary>
/// <remarks>
/// <para>
/// Under prepare-time binding there are none: every scalar was folded into a literal before the plan
/// existed. Under execute-time binding a scalar is a <c>DynamicParam</c> carrying a
/// <c>bound_key</c> — its name — and no index, because it is not one of the statement's own
/// parameters and does not belong among their types.
/// </para>
/// <para>
/// Something has to give it a place in the bound values all the same, and this is the one thing that
/// does: the names in the order a walk of the plan first meets them, taking the slots that follow the
/// statement's own parameters. The compiler numbers its parameter expressions by it and the client
/// fills the values by it, and because both read it off the same plan they cannot disagree.
/// </para>
/// </remarks>
public static class BoundScalars
{
    /// <summary>One entry per distinct name, in the order the plan first mentions it.</summary>
    public static IReadOnlyList<BoundScalar> Of(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<BoundScalar>? found = null;
        foreach (var expr in PlanWalker.AllExprs(plan))
        {
            if (expr.KindCase != Expr.KindOneofCase.Param || expr.Param.BoundKey.Length == 0)
            {
                continue;
            }

            found ??= [];
            var name = expr.Param.BoundKey;
            var known = false;
            foreach (var scalar in found)
            {
                known |= string.Equals(scalar.Name, name, StringComparison.Ordinal);
            }

            if (!known)
            {
                found.Add(new BoundScalar(name, expr.Type));
            }
        }

        return found ?? (IReadOnlyList<BoundScalar>)[];
    }
}

/// <summary>One context scalar a plan reads at execution: the name the host bound, and its type.</summary>
/// <param name="Name">The <c>bound_key</c> on every <c>DynamicParam</c> that reads it.</param>
/// <param name="Type">The type the shape declared at prepare, which the value must fit.</param>
public sealed record BoundScalar(string Name, Type Type);
