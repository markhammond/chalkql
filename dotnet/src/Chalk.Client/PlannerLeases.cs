using System.Runtime.CompilerServices;

namespace Chalk.Client;

/// <summary>
/// The engines using each planner client (F290). Engines may share one: each mints its own instance
/// id, so the planner keeps their catalogs apart (D271). The client is disposed with the last engine
/// that used it, so an engine made with a client of its own still disposes it, as it always has, and
/// one disposed while another uses the same client leaves that engine working.
/// </summary>
/// <remarks>
/// A client disposed that way is retired: an engine made on it afterwards is refused at once, naming
/// the client, rather than failing later at the first refresh that asks the planner something.
/// </remarks>
internal static class PlannerLeases
{
    private sealed class Lease
    {
        public int Engines;
        public bool Retired;
    }

    private static readonly ConditionalWeakTable<IQueryPlanner, Lease> Leases = new();

    /// <summary>One more engine uses <paramref name="planner"/>; refused for a retired client.</summary>
    public static void Acquire(IQueryPlanner planner)
    {
        var lease = Leases.GetValue(planner, static _ => new Lease());
        lock (lease)
        {
            if (lease.Retired)
            {
                throw new ArgumentException(
                    "ChalkEngineOptions.Planner was disposed with the last engine that used it, so no engine "
                    + "can plan with it. Engines may share a planner client while any of them is alive; an "
                    + "engine made after the last one is gone needs a client of its own, such as another "
                    + "PlannerProcess.CreatePlanner().",
                    "options");
            }

            lease.Engines++;
        }
    }

    /// <summary>
    /// One engine fewer uses <paramref name="planner"/>. With <paramref name="retire"/>, whether it was
    /// the last, in which case the client is retired and its caller disposes it.
    /// </summary>
    public static bool Release(IQueryPlanner planner, bool retire)
    {
        if (!Leases.TryGetValue(planner, out var lease))
        {
            return retire;
        }

        lock (lease)
        {
            lease.Engines--;
            if (lease.Engines > 0 || !retire)
            {
                return false;
            }

            lease.Retired = true;
            return true;
        }
    }
}
