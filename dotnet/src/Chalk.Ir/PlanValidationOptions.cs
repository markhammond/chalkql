namespace Chalk.Ir;

/// <summary>Knobs for <see cref="PlanValidator"/>. The defaults are what a client uses on plan receipt.</summary>
public sealed class PlanValidationOptions
{
    /// <summary>The defaults: verify the digest, reject a pushed <c>Read.filter</c>.</summary>
    public static PlanValidationOptions Default { get; } = new();

    /// <summary>
    /// Recompute <c>plan_digest</c> and compare (I-IR-9). Off only when the digest is being computed
    /// rather than checked — the recorder, and the planner's own self-test.
    /// </summary>
    public bool VerifyDigest { get; init; } = true;

    /// <summary>
    /// Permit <c>Read.filter</c> (I-IR-6). An M1–M3 planner never sets it; from M4 the client sets
    /// this when the live catalog says the source declared a matching capability.
    /// </summary>
    public bool AllowReadFilter { get; init; }

    /// <summary>
    /// The client's own catalog, as the one question <b>I-IR-E</b> asks of it: how many columns does
    /// this table have, when it carries an entitlement, and null when it does not
    /// (<c>docs/design/16-entitlements.md</c> §3.10, D201).
    /// </summary>
    /// <remarks>
    /// It is the client's catalog and never the planner's claim: what the invariant re-establishes
    /// is that <em>every</em> read of a table this host entitled went through the enforcement
    /// rewrite, which a plan that skipped one could not tell it. Left null, the invariant does not
    /// run — a caller that holds no catalog has nothing to re-establish from.
    /// </remarks>
    public Func<TableRef, int?>? EntitledTables { get; init; }

    /// <summary>
    /// What the planner's report says each output column discloses, in output order, for I-IR-E's
    /// last clause: the client recomputes the same labels from the reads' own verdicts by walking
    /// the physical plan, and a disagreement is a refusal before anything executes.
    /// </summary>
    /// <remarks>
    /// The two are computed from different things — the report on the pre-optimisation logical tree,
    /// this walk on the plan that will run — which is exactly why comparing them is worth anything.
    /// The Arrow field metadata is written from the same list, so agreeing with the report is
    /// agreeing with it.
    /// </remarks>
    public IReadOnlyList<DisclosureOutcome>? ReportedDisclosures { get; init; }

    /// <summary>
    /// The descriptor hash the report named for one entitled table, or null where the caller has no
    /// report to compare against (D231, <c>docs/design/16-entitlements.md</c> §3.13).
    /// </summary>
    /// <remarks>
    /// Every entitled read carries the hash of the descriptor it was compiled under, and the digest
    /// covers it, so a changed policy is a different plan by construction. What this adds is the
    /// client's own check that the plan in its hand and the report beside it speak about the same
    /// descriptor — which only a caller holding both can ask, and is why it is an option rather than
    /// a clause the core always runs.
    /// </remarks>
    public Func<TableRef, string?>? ReportedDescriptorHashes { get; init; }
}
