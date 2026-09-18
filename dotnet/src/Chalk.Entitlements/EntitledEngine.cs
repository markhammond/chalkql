using Chalk.Client;

namespace Chalk.Entitlements;

/// <summary>
/// The one way to attach a policy to an engine
/// (<c>docs/design/29-entitlements-as-a-wrapper.md</c> §3, D213).
/// </summary>
public static class ChalkEngineEntitlements
{
    /// <summary>
    /// Wraps an engine so every statement prepared through the wrapper carries these entitlement
    /// options, and every prepared query hands back what the policy did.
    /// </summary>
    /// <param name="engine">The engine to wrap. It is not modified, and is still usable directly.</param>
    /// <param name="options">
    /// What this host wants done with entitled columns it cannot disclose, and with stars over
    /// entitled tables (§6). Every default is what a statement gets when nothing is said.
    /// </param>
    /// <param name="audit">
    /// A host's observer of entitled executions (D157, D205). Null — the default — raises nothing
    /// and builds nothing: no audit event unless subscribed (§0).
    /// </param>
    public static EntitledEngine WithEntitlements(
        this ChalkEngine engine, EntitlementsOptions? options = null, IEntitlementsAudit? audit = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new EntitledEngine(engine, options ?? new EntitlementsOptions(), audit);
    }
}

/// <summary>
/// An engine with a policy on it: prepare through this and every request carries the options as an
/// extension, and every answer carries the report back (D212, D213).
/// </summary>
/// <remarks>
/// Engine-shaped rather than an <c>IChalkEngine</c> implementation, because the thing being wrapped
/// is one method and one answer: <see cref="PrepareAsync"/> attaches, <see cref="EntitledQuery"/>
/// exposes. Everything else — executing, refreshing the catalog, the arena pool — is the engine's
/// own and is reached through <see cref="Engine"/>, which is the same object a host that never
/// referenced this package would have.
/// </remarks>
public sealed class EntitledEngine
{
    private readonly IEntitlementsAudit? _audit;

    internal EntitledEngine(ChalkEngine engine, EntitlementsOptions options, IEntitlementsAudit? audit)
    {
        Engine = engine;
        Options = options;
        _audit = audit;
    }

    /// <summary>The engine underneath. Execution, catalog refresh and everything else go through it.</summary>
    public ChalkEngine Engine { get; }

    /// <summary>The options every prepare through this wrapper attaches.</summary>
    public EntitlementsOptions Options { get; }

    /// <summary>
    /// Plans and compiles a statement with this policy and these bound values, and returns what the
    /// policy did to it.
    /// </summary>
    public async ValueTask<EntitledQuery> PrepareAsync(
        string sql,
        RequestContext? context = null,
        PrepareOptions? options = null,
        CancellationToken ct = default)
    {
        var prepared = await Engine
            .PrepareAsync(sql, context, With(options), ct)
            .ConfigureAwait(false);
        return new EntitledQuery(this, sql, prepared, _audit);
    }

    /// <summary>
    /// What this principal's policy resolves to for a statement, without executing it and without
    /// the caller needing a plan (<c>docs/design/16-entitlements.md</c> §3.12, D207).
    /// </summary>
    /// <remarks>
    /// Per entitled table the folded row predicate as SQL text, whether it reached the source and
    /// which of its conjuncts stayed local; per column the folded disclosure — a constant name where
    /// the fold settled it, and the folded rule conditions where it varies by row — the mask, the
    /// stand-in and the group-size floor. It is computed by the same pass that enforces the policy,
    /// from the same folded expressions, so it cannot drift from what runs: that is the whole of its
    /// value as an oracle.
    /// </remarks>
    public async ValueTask<EntitlementsExplanation> ExplainAsync(
        string sql,
        RequestContext? context = null,
        PrepareOptions? options = null,
        CancellationToken ct = default)
    {
        var extensions = await Engine
            .PlanExtensionsAsync(sql, context, With(options, explain: true), ct)
            .ConfigureAwait(false);
        return Disclosures.Explanation(Disclosures.Find<Rpc.EntitlementsExplain>(extensions));
    }

    /// <summary>These prepare options with the entitlement extension added to their bag.</summary>
    private PrepareOptions With(PrepareOptions? options, bool explain = false)
    {
        options ??= new PrepareOptions();
        return new PrepareOptions
        {
            Pushdown = options.Pushdown,
            IncludePlanText = options.IncludePlanText,
            ParameterTypes = options.ParameterTypes,
            Conformance = options.Conformance,
            Libraries = options.Libraries,
            DisabledCapabilities = options.DisabledCapabilities,
            JoinPolicy = options.JoinPolicy,
            Extensions = [.. options.Extensions, Options.ToProto(explain)],
        };
    }
}

/// <summary>
/// A prepared statement and what the policy did to it (§3.12). Executes through the engine like any
/// other: <c>engine.ExecuteAsync(entitled, …)</c> takes it directly.
/// </summary>
public sealed class EntitledQuery
{
    private readonly EntitledEngine _engine;
    private readonly string _sql;

    private readonly IEntitlementsAudit? _audit;

    internal EntitledQuery(
        EntitledEngine engine, string sql, PreparedQuery prepared, IEntitlementsAudit? audit)
    {
        _engine = engine;
        _sql = sql;
        _audit = audit;
        Query = prepared;
        Entitlements = Disclosures.Report(prepared);

        // The plan against the report (I-IR-E's last clause, §3.10, D201). The core validated
        // everything its own catalog can answer for before it compiled; this is the clause that
        // needs the report, so the package that has the report is the one that asks for it (D213).
        if (Entitlements.Tables.Count > 0)
        {
            Chalk.Ir.PlanValidator.Validate(
                prepared.Plan,
                new Chalk.Ir.PlanValidationOptions
                {
                    EntitledTables = engine.Engine.EntitledColumnCount,
                    ReportedDisclosures = Disclosures.Outcomes(prepared),
                    ReportedDescriptorHashes = DescriptorHashOf,
                });
        }

        // `chalk.disclosure` on every field that discloses anything but the value itself, and the
        // audit event before each execution. Both through the one seam the core keeps for an
        // extension; a plan with nothing to say leaves the compiler's own schema by reference.
        prepared.Decorate(
            schema => Disclosures.Decorate(schema, Entitlements.Columns),
            audit is null || Entitlements.Tables.Count == 0 ? null : q => Audit(audit, q));
    }

    /// <summary>
    /// The descriptor hash the report names for one table, or null where it names none (D231).
    /// </summary>
    /// <remarks>
    /// The read carries the hash of the descriptor it was compiled under and the digest covers it,
    /// so a changed policy is a different plan by construction; this is the client asking that the
    /// plan in its hand and the report beside it speak about the same one.
    /// </remarks>
    private string? DescriptorHashOf(Chalk.Ir.TableRef table)
    {
        // Keyed by (schema, table), and the schema is checked first. Two sources may hold a table
        // of one name under one policy, each with its own descriptor, so a match on the bare name
        // would hand the first one's hash to the second one's read — and the check this feeds is
        // exactly the one that says the plan and the report speak about the same descriptor (D231).
        foreach (var reported in Entitlements.Tables)
        {
            if (string.Equals(reported.Schema, table.Schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reported.Table, table.Table, StringComparison.OrdinalIgnoreCase))
            {
                return reported.DescriptorHash;
            }
        }

        // A report that names no schema at all is one this client did not plan against a catalog
        // that could tell two of them apart; the bare name is then all there is, and it is enough.
        foreach (var reported in Entitlements.Tables)
        {
            if (reported.Schema.Length == 0
                && string.Equals(reported.Table, table.Table, StringComparison.OrdinalIgnoreCase))
            {
                return reported.DescriptorHash;
            }
        }

        return null;
    }

    /// <summary>The prepared statement itself.</summary>
    public PreparedQuery Query { get; }

    /// <summary>What the entitlement rewrite did (§3.12).</summary>
    public EntitlementsReport Entitlements { get; }

    /// <summary>
    /// One per output column, in order, with what it discloses. Every column reads
    /// <see cref="ReportedDisclosure.Full"/> for a plan over a catalog without entitlements.
    /// </summary>
    public IReadOnlyList<EntitledColumn> Columns => Entitlements.Columns;

    /// <summary>The plan for the shape prepared at prepare time.</summary>
    public Chalk.Ir.Plan Plan => Query.Plan;

    public ulong PlanDigest => Query.PlanDigest;

    /// <summary>The Arrow schema every produced batch matches, disclosure metadata and all.</summary>
    public Apache.Arrow.Schema OutputSchema => Query.OutputSchema;

    /// <summary>What the policy resolved to for this statement, in the policy's own terms (D207).</summary>
    public ValueTask<EntitlementsExplanation> ExplainAsync(CancellationToken ct = default) =>
        _engine.ExplainAsync(_sql, Query.Context, options: null, ct);

    /// <summary>
    /// This statement planned again with <paramref name="more"/>'s bindings folded into what this
    /// plan left open, and the policy's answer for the narrower plan (§2.1, D233).
    /// </summary>
    /// <remarks>
    /// The narrowed plan is identical to preparing with the union of the two bindings from the
    /// start, and so is the report beside it: what the fold settles is settled, and every label the
    /// narrowing turns from <c>PerRow</c> into a constant is one the folded plan would have carried
    /// too. A value this plan already folded is refused naming it.
    /// </remarks>
    public async ValueTask<EntitledQuery> NarrowAsync(
        RequestContext more, CancellationToken ct = default)
    {
        var narrowed = await Query.NarrowAsync(more, ct).ConfigureAwait(false);
        return new EntitledQuery(_engine, _sql, narrowed, _audit);
    }

    /// <summary>So an entitled query goes wherever a prepared one does.</summary>
    public static implicit operator PreparedQuery(EntitledQuery entitled)
    {
        ArgumentNullException.ThrowIfNull(entitled);
        return entitled.Query;
    }

    /// <summary>The same, spelled out for a language that has no implicit conversions.</summary>
    public PreparedQuery ToPreparedQuery() => Query;

    /// <summary>
    /// One event per execution, before anything runs (D157, D205). It carries what identifies the
    /// policy and the request and never a context value: the digest, the descriptor hashes, the row
    /// counts of the bound lists and the host's own purpose and actor.
    /// </summary>
    private void Audit(IEntitlementsAudit observer, PreparedQuery query)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (query.Context is { } context)
        {
            foreach (var (name, list) in context.Lists)
            {
                counts[name] = list.Rows.Count;
            }

            foreach (var (name, relation) in context.Relations)
            {
                counts[name] = relation.Rows.Count;
            }
        }

        observer.Executed(new EntitlementsAuditEvent
        {
            PlanDigest = query.PlanDigest,
            DescriptorHashes = Entitlements.DescriptorHashes,
            Tables = Entitlements.Tables,
            ContextListRowCounts = counts,
            Purpose = query.Context?.Purpose ?? "",
            Actor = query.Context?.Actor ?? "",
        });
    }
}
