namespace Chalk.Execution.Maintenance;

/// <summary>
/// How a maintained state's stores grow as rows arrive: by half again, not twice (F270). A store built
/// from a whole result fits it exactly, and grows the first time a view outgrows it; doubling left a
/// long-lived view holding up to twice what a fresh one held, and half again caps that near half, for a
/// few more copies as a store grows, still a constant cost a row.
/// </summary>
internal static class StateGrowth
{
    /// <summary>The next length for a store of <paramref name="current"/> that needs at least <paramref name="needed"/>.</summary>
    public static int Next(int current, int needed, int least = 16)
    {
        var grown = current + (current >> 1);
        return Math.Max(needed, Math.Max(least, grown));
    }
}
