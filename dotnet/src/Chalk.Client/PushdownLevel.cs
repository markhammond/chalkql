namespace Chalk.Client;

/// <summary>
/// How much work the planner may hand to a source. One value per <c>chalk.v1.PushdownLevel</c>
/// constant, chosen per statement through <see cref="PrepareOptions.Pushdown"/>.
/// </summary>
/// <remarks>
/// Hand-written rather than the generated wire enum, for the same reason
/// <see cref="SqlConformance"/> is (ADR 0014): the transport's types are an implementation detail,
/// and a host should not have to reference <c>Chalk.Client.Rpc</c> to name a pushdown level. The two
/// are twins, value for value, and <c>GrpcQueryPlanner</c> maps between them with a cast it checks.
/// </remarks>
public enum PushdownLevel
{
    /// <summary>Everything the planner and the sources can do between them. The default.</summary>
    Full = 1,

    /// <summary>Predicates only. Reserved for M4; the same as <see cref="None"/> before it.</summary>
    FiltersOnly = 2,

    /// <summary>Column pruning only: a scan reads fewer columns, and nothing else is pushed.</summary>
    ProjectionOnly = 3,

    /// <summary>
    /// The reference (I4) configuration: every <c>Read</c> has a full projection and no filter, and
    /// no <c>IndexLookup</c> or <c>RemoteQuery</c> is emitted.
    /// </summary>
    None = 4,
}
