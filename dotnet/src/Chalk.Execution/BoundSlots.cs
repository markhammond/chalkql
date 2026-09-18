using Chalk.Ir;

namespace Chalk.Execution;

/// <summary>
/// Where a plan's named bound scalars sit among an execution's values
/// (<c>docs/design/16-entitlements.md</c> §2, D209).
/// </summary>
/// <remarks>
/// The statement's own parameters come first, numbered as the planner numbered them; the plan's bound
/// scalars follow, in the order <see cref="Chalk.Ir.BoundScalars"/> reads off the plan. So every
/// parameter — the caller's and the context's alike — is one index into one array, and nothing in the
/// executor has to know which kind it was holding.
/// </remarks>
internal static class BoundSlots
{
    /// <summary>What a plan under prepare-time binding needs: nothing at all.</summary>
    public static IReadOnlyDictionary<string, int> None { get; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The slots of <paramref name="bound"/>, after <paramref name="statementParameters"/>.</summary>
    public static IReadOnlyDictionary<string, int> Of(
        IReadOnlyList<BoundScalar> bound, int statementParameters)
    {
        if (bound.Count == 0)
        {
            return None;
        }

        var slots = new Dictionary<string, int>(bound.Count, StringComparer.Ordinal);
        for (var i = 0; i < bound.Count; i++)
        {
            slots[bound[i].Name] = statementParameters + i;
        }

        return slots;
    }

    /// <summary>The index this parameter reads: its own, or the slot its name was given.</summary>
    public static int Of(DynamicParam param, IReadOnlyDictionary<string, int> slots)
    {
        if (param.BoundKey.Length == 0)
        {
            return (int)param.Index;
        }

        if (!slots.TryGetValue(param.BoundKey, out var slot))
        {
            throw new InvalidPlanException(
                "I-IR-3",
                "param",
                $"a DynamicParam names the bound value '{param.BoundKey}' and the plan lists none, "
                + "so there is no value for it to read (docs/design/16-entitlements.md §2)");
        }

        return slot;
    }
}
