namespace Chalk.Ir;

/// <summary>
/// <b>I-IR-E</b>, the client-side entitlement invariant (<c>docs/design/16-entitlements.md</c> §3.10,
/// D201): what a client re-establishes over the IR it received, from its <em>own</em> catalog,
/// before anything executes.
/// </summary>
/// <remarks>
/// <para>
/// The planner proves the model of its own plan and reports on it. That report is computed on the
/// pre-Hep logical tree and applied through <c>RelRoot.fields</c> — the same index list the root
/// projection uses on the post-Volcano row — so a field-index defect in Calcite would move the
/// report and the projection together and they could not disagree on their own
/// (<c>docs/design/calcite-open-issues-assessment.md</c>). This walk over the physical IR is the
/// independent witness: it recomputes every output column's label from the reads' own verdicts and
/// refuses a plan whose report disagrees.
/// </para>
/// <para>
/// <b>What it covers is escapes</b> — a raw value reaching a consumer the disclosures forbid, a read
/// of an entitled table the pass never touched, a withheld column at the root. <b>What it does not
/// cover is misclassification</b>: a pass that declared a withheld column full would pass this
/// vacuously, and deriving the verdicts independently would be the pass written a second time in C#.
/// That is the corpus's job, and the tenancy package's <c>Reconcile</c>.
/// </para>
/// <para>
/// A relation kind the walk does not know fails the invariant. An unknown node is not an absent one:
/// a column origin nobody can resolve is a column nobody can vouch for.
/// </para>
/// </remarks>
internal static class EntitlementsInvariant
{
    private const string Invariant = "I-IR-E";

    /// <summary>
    /// The aggregates that report a population rather than an individual (D190), in the IR's own
    /// vocabulary. The client's copy of <c>Chalk.Catalog.PopulationAggregates</c>, which this
    /// assembly is below and cannot name; the two are held together by
    /// <c>PopulationAggregateParityTests</c>.
    /// </summary>
    private static readonly HashSet<AggregateFunctionId> Population =
    [
        AggregateFunctionId.Count,
        AggregateFunctionId.Sum,
        AggregateFunctionId.Sum0,
        AggregateFunctionId.Avg,
        AggregateFunctionId.BoolAnd,
        AggregateFunctionId.BoolOr,
        AggregateFunctionId.ApproxCountDistinct,
    ];

    /// <summary>
    /// Every relation kind the origin walk can trace a column through. A kind added to the IR and
    /// not to this set fails the invariant rather than being traced wrongly, and
    /// <c>EntitlementsInvariantCoverageTests</c> breaks the build the day one is.
    /// </summary>
    internal static readonly HashSet<Rel.KindOneofCase> Traced =
    [
        Rel.KindOneofCase.Read,
        Rel.KindOneofCase.IndexLookup,
        Rel.KindOneofCase.VirtualTable,
        Rel.KindOneofCase.BoundTable,
        Rel.KindOneofCase.TableFunctionScan,
        Rel.KindOneofCase.MaterialisedInput,
        Rel.KindOneofCase.RemoteQuery,
        Rel.KindOneofCase.Filter,
        Rel.KindOneofCase.Sort,
        Rel.KindOneofCase.TopN,
        Rel.KindOneofCase.Fetch,
        Rel.KindOneofCase.Project,
        Rel.KindOneofCase.Aggregate,
        Rel.KindOneofCase.HashAggregate,
        Rel.KindOneofCase.StreamAggregate,
        Rel.KindOneofCase.Join,
        Rel.KindOneofCase.HashJoin,
        Rel.KindOneofCase.MergeJoin,
        Rel.KindOneofCase.NestedLoopJoin,
        Rel.KindOneofCase.AsOfJoin,
        Rel.KindOneofCase.LookupJoin,
        Rel.KindOneofCase.AdaptiveJoin,
        Rel.KindOneofCase.SetOp,
        Rel.KindOneofCase.PartitionedScan,
        Rel.KindOneofCase.Window,
        Rel.KindOneofCase.Hop,
        Rel.KindOneofCase.Session,
        Rel.KindOneofCase.Unnest,
    ];

    /// <summary>One column of one entitled read, as the walk carries it upward.</summary>
    /// <param name="Correlation">
    /// Whether this is the entitlement mechanism's own read along a declared path (D265,
    /// <c>docs/design/38-existential-visibility.md</c> §2, §7) rather than a leaf of the statement.
    /// It discloses nothing at all, so the walk carries it as <c>Redacted</c> and clause (c) refuses
    /// it at the root — which is the whole of "the mechanism's occurrence is unreachable from the
    /// statement".
    /// </param>
    private readonly record struct Origin(
        Read Read, int TableColumn, bool Derived, bool Correlation = false)
    {
        internal DisclosureOutcome Outcome
        {
            get
            {
                if (Correlation)
                {
                    return DisclosureOutcome.Redacted;
                }

                foreach (var disclosure in Read.Disclosures)
                {
                    if (disclosure.Column == TableColumn)
                    {
                        return disclosure.Outcome;
                    }
                }

                return DisclosureOutcome.Full;
            }
        }

        internal bool Statistical
        {
            get
            {
                foreach (var disclosure in Read.Disclosures)
                {
                    if (disclosure.Column == TableColumn)
                    {
                        return disclosure.Statistical;
                    }
                }

                return false;
            }
        }
    }

    internal static void Check(Plan plan, PlanValidationOptions options)
    {
        if (options.EntitledTables is null)
        {
            // No catalog was handed over, so there is nothing to re-establish from. A host that
            // wants the invariant gives the validator the catalog it registered.
            return;
        }

        var state = new Walk(options);
        state.Reads(plan.Root, "root");
        if (!state.AnyEntitled)
        {
            return;
        }

        var atRoot = state.Of(plan.Root, "root");
        state.Tainted(plan.Root, "root");
        state.RootColumns(plan, atRoot);
        state.Labels(plan, atRoot);
    }

    private sealed class Walk(PlanValidationOptions options)
    {
        private readonly Dictionary<Rel, IReadOnlyList<HashSet<Origin>>> _cache = new(Same.Instance);

        internal bool AnyEntitled { get; private set; }

        /// <summary>
        /// Clauses (a) and (d), from the client's own catalog: every read of an entitled table — in
        /// the top-level plan and inside every pushed plan — carries the pass's mark and exactly one
        /// verdict per column of the table.
        /// </summary>
        internal void Reads(Rel? rel, string path)
        {
            if (rel is null)
            {
                return;
            }

            var here = $"{path}/{rel.KindCase}";
            if (rel.KindCase == Rel.KindOneofCase.Read)
            {
                var read = rel.Read;
                var columns = options.EntitledTables!(read.Table);
                if (columns is null)
                {
                    if (read.Disclosures.Count > 0)
                    {
                        throw new InvalidPlanException(
                            Invariant,
                            here,
                            $"the read of {Name(read.Table)} carries per-column disclosures and this "
                            + "client's catalog says the table has no entitlement");
                    }

                    return;
                }

                if (read.Correlation)
                {
                    // The mechanism's own occurrence along a declared path (D265 §2, §7): raw,
                    // disclosing nothing, and held below to being unreachable from the statement.
                    // It is the rewrite's, so it still carries the mark every injected node does.
                    AnyEntitled = true;
                    if (!rel.PolicyInjected)
                    {
                        throw new InvalidPlanException(
                            Invariant,
                            here,
                            $"the read of {Name(read.Table)} says it is the entitlement mechanism's "
                            + "own correlation along a declared path, and the plan does not mark it "
                            + "as the rewrite's");
                    }

                    if (read.Disclosures.Count > 0 || read.DescriptorHash.Length > 0)
                    {
                        throw new InvalidPlanException(
                            Invariant,
                            here,
                            $"the read of {Name(read.Table)} says it is the entitlement mechanism's "
                            + "own correlation along a declared path and carries breadcrumbs; a "
                            + "correlation read discloses nothing, so it states no verdict at all");
                    }

                    return;
                }

                AnyEntitled = true;
                if (!rel.PolicyInjected)
                {
                    throw new InvalidPlanException(
                        Invariant,
                        here,
                        $"{Name(read.Table)} carries an entitlement in this client's catalog and the "
                        + "plan reads it without the enforcement rewrite, so nothing sanitises it");
                }

                if (read.Disclosures.Count != columns)
                {
                    throw new InvalidPlanException(
                        Invariant,
                        here,
                        $"the read of {Name(read.Table)} states {read.Disclosures.Count} column "
                        + $"verdicts and the table has {columns} columns; an entitled read states one "
                        + "per column, in ordinal order");
                }

                for (var i = 0; i < read.Disclosures.Count; i++)
                {
                    if (read.Disclosures[i].Column != i)
                    {
                        throw new InvalidPlanException(
                            Invariant,
                            here,
                            $"the read of {Name(read.Table)} states column "
                            + $"{read.Disclosures[i].Column} at position {i}; the verdicts are in the "
                            + "table's own ordinal order");
                    }
                }

                // (f) The descriptor the read was compiled under is the one the report named for the
                // same table (D231). The digest covers the read, so a plan built against a policy
                // that has since changed is a different plan; this is the client checking that the
                // plan in its hand and the report beside it speak about one descriptor.
                var reported = options.ReportedDescriptorHashes?.Invoke(read.Table);
                if (reported is not null
                    && !string.Equals(reported, read.DescriptorHash, StringComparison.Ordinal))
                {
                    throw new InvalidPlanException(
                        Invariant,
                        here,
                        $"the read of {Name(read.Table)} was compiled under descriptor "
                        + $"'{(read.DescriptorHash.Length == 0 ? "<none>" : read.DescriptorHash)}' "
                        + $"and the report names '{reported}' for the same table; the plan and the "
                        + "report do not speak about one policy");
                }

                return;
            }

            foreach (var pushed in PlanWalker.Pushed(rel))
            {
                Reads(pushed, here + "/pushed");
            }

            foreach (var input in PlanWalker.Inputs(rel))
            {
                Reads(input, here);
            }
        }

        /// <summary>Clause (c): no root output column <em>is</em> a column this principal may not see.</summary>
        internal void RootColumns(Plan plan, IReadOnlyList<HashSet<Origin>> atRoot)
        {
            for (var i = 0; i < atRoot.Count; i++)
            {
                foreach (var origin in atRoot[i])
                {
                    if (origin.Derived)
                    {
                        continue;
                    }

                    if (origin.Outcome is DisclosureOutcome.Masked
                        or DisclosureOutcome.Redacted
                        or DisclosureOutcome.PerRow
                        or DisclosureOutcome.Tested)
                    {
                        throw new InvalidPlanException(
                            Invariant,
                            $"plan.output_type[{i}]",
                            $"output column '{Field(plan, i)}' is column {origin.TableColumn} of "
                            + $"{Name(origin.Read.Table)} itself, which this plan's own read says is "
                            + $"{origin.Outcome} — "
                            + (origin.Correlation
                                ? "the entitlement mechanism's own correlation along a declared "
                                  + "path, which discloses nothing and must be unreachable from the "
                                  + "statement (D265 §7)"
                                : "redacted and unsanitised"));
                    }
                }
            }
        }

        /// <summary>
        /// Clause (e): every output column's label recomputed from the reads' verdicts, and compared
        /// with the one the planner reported.
        /// </summary>
        internal void Labels(Plan plan, IReadOnlyList<HashSet<Origin>> atRoot)
        {
            var reported = options.ReportedDisclosures;
            if (reported is null)
            {
                return;
            }

            if (reported.Count != atRoot.Count)
            {
                throw new InvalidPlanException(
                    Invariant,
                    "plan.output_type",
                    $"the report states {reported.Count} column disclosures and the plan's root row "
                    + $"has {atRoot.Count} columns");
            }

            for (var i = 0; i < atRoot.Count; i++)
            {
                if (atRoot[i].Count == 0)
                {
                    // The walk has nothing to say. A withheld column is a *constant* by the time
                    // there is a plan — a typed NULL, the type's zero, the host's own stand-in — and
                    // a constant has no column origin at all, so the one label a caller most needs is
                    // exactly the one this clause cannot recompute (ADR 0025 V64). Clause (c) is what
                    // guards that direction: it refuses a withheld column reaching the root as
                    // itself, which is the escape.
                    continue;
                }

                var walked = Meet(atRoot[i]);
                if (Rank(reported[i]) >= Rank(walked))
                {
                    // The report is no more permissive than what the plan's own reads show, which is
                    // the direction that matters. It is legitimately *stricter* in two places the
                    // report knows about and an origin walk cannot: a set operation's branches are
                    // alternative rows rather than two origins of one value, and a window function is
                    // judged on its arguments (V66).
                    continue;
                }

                throw new InvalidPlanException(
                    Invariant,
                    $"plan.output_type[{i}]",
                    $"output column '{Field(plan, i)}' is reported {reported[i]} and the reads this "
                    + $"plan walks say {walked}; the report claims more disclosure than the plan's "
                    + "own per-column verdicts allow");
            }
        }

        /// <summary>
        /// The meet over a column's origins, in the report's own order: full when every origin is,
        /// masked when any is masked and none worse, redacted when any is redacted, per-row when
        /// any is, and aggregate for a guarded population value.
        /// </summary>
        private static DisclosureOutcome Meet(HashSet<Origin> origins)
        {
            var meet = DisclosureOutcome.Full;
            foreach (var origin in origins)
            {
                meet = Worse(meet, origin.Outcome);
            }

            return meet;
        }

        private static DisclosureOutcome Worse(DisclosureOutcome left, DisclosureOutcome right) =>
            Rank(left) >= Rank(right) ? left : right;

        private static int Rank(DisclosureOutcome outcome) => outcome switch
        {
            DisclosureOutcome.Full => 0,
            DisclosureOutcome.Masked => 1,
            DisclosureOutcome.Aggregate => 2,
            DisclosureOutcome.PerRow => 3,
            // Tested and Redacted are the two ends of the same answer: the value itself is a
            // placeholder under both, and Tested additionally discloses one bit the policy named
            // (D261). So a report saying Tested where the walk says Redacted is the one direction
            // this clause refuses, and Redacted over a Tested origin is honestly stricter.
            DisclosureOutcome.Tested => 4,
            _ => 5,
        };

        // ---------------------------------------------------------------- the origin walk

        /// <summary>Which entitled column each of {@code rel}'s output columns reads, and how.</summary>
        internal IReadOnlyList<HashSet<Origin>> Of(Rel rel, string path)
        {
            if (_cache.TryGetValue(rel, out var cached))
            {
                return cached;
            }

            var here = $"{path}/{rel.KindCase}";
            var origins = Compute(rel, here);
            _cache[rel] = origins;
            return origins;
        }

        private IReadOnlyList<HashSet<Origin>> Compute(Rel rel, string path)
        {
            if (!Traced.Contains(rel.KindCase))
            {
                throw new InvalidPlanException(
                    Invariant,
                    path,
                    $"the relation kind {rel.KindCase} is one this client's entitlement invariant "
                    + "does not know how to trace a column origin through, so it cannot vouch for "
                    + "what the plan discloses");
            }

            var width = rel.RowType.Fields.Count;
            switch (rel.KindCase)
            {
                case Rel.KindOneofCase.Read:
                    return Leaf(rel.Read, rel.Read.Projection, width);

                case Rel.KindOneofCase.IndexLookup:
                    // A lookup is a read of the same table by another path; the wrapper the pass put
                    // on it travels with it, so the planner writes the same verdicts on the Read the
                    // lookup replaced. This node carries none of its own and reads nothing entitled.
                    return None(width);

                case Rel.KindOneofCase.VirtualTable:
                case Rel.KindOneofCase.BoundTable:
                case Rel.KindOneofCase.TableFunctionScan:
                case Rel.KindOneofCase.MaterialisedInput:
                    return None(width);

                case Rel.KindOneofCase.RemoteQuery:
                    return rel.RemoteQuery.PushedPlan is { } pushed
                        ? Widen(Of(pushed, path + "/pushed"), width)
                        : None(width);

                case Rel.KindOneofCase.Filter:
                    return Widen(Of(rel.Filter.Input, path), width);

                case Rel.KindOneofCase.Sort:
                    return Widen(Of(rel.Sort.Input, path), width);

                case Rel.KindOneofCase.TopN:
                    return Widen(Of(rel.TopN.Input, path), width);

                case Rel.KindOneofCase.Fetch:
                    return Widen(Of(rel.Fetch.Input, path), width);

                case Rel.KindOneofCase.Project:
                    return Projected(Of(rel.Project.Input, path), rel.Project.Exprs, width);

                case Rel.KindOneofCase.Aggregate:
                    return Aggregated(rel.Aggregate, path, width);

                case Rel.KindOneofCase.HashAggregate:
                    return Aggregated(rel.HashAggregate.Aggregate, path, width);

                case Rel.KindOneofCase.StreamAggregate:
                    return Aggregated(rel.StreamAggregate.Aggregate, path, width);

                case Rel.KindOneofCase.Join:
                    return Joined(rel.Join.Left, rel.Join.Right, rel.Join.Type, path, width);

                case Rel.KindOneofCase.HashJoin:
                    return Joined(rel.HashJoin.Left, rel.HashJoin.Right, rel.HashJoin.Type, path, width);

                case Rel.KindOneofCase.MergeJoin:
                    return Joined(
                        rel.MergeJoin.Left, rel.MergeJoin.Right, rel.MergeJoin.Type, path, width);

                case Rel.KindOneofCase.NestedLoopJoin:
                    return Joined(
                        rel.NestedLoopJoin.Left,
                        rel.NestedLoopJoin.Right,
                        rel.NestedLoopJoin.Type,
                        path,
                        width);

                case Rel.KindOneofCase.AsOfJoin:
                    return Joined(
                        rel.AsOfJoin.Left, rel.AsOfJoin.Right, rel.AsOfJoin.Type, path, width);

                case Rel.KindOneofCase.LookupJoin:
                    return Joined(
                        rel.LookupJoin.Driving, rel.LookupJoin.Lookup, rel.LookupJoin.Type, path, width);

                case Rel.KindOneofCase.AdaptiveJoin:
                    // The two alternatives produce the same row, so a column's origins are both's.
                    return Union(
                        Of(Wrap(rel.AdaptiveJoin.Lookup), path + "/lookup"),
                        Of(rel.AdaptiveJoin.Local, path + "/local"),
                        width);

                case Rel.KindOneofCase.SetOp:
                {
                    var origins = None(width);
                    foreach (var input in rel.SetOp.Inputs)
                    {
                        var branch = Of(input, path);
                        for (var i = 0; i < width && i < branch.Count; i++)
                        {
                            origins[i].UnionWith(branch[i]);
                        }
                    }

                    return origins;
                }

                case Rel.KindOneofCase.PartitionedScan:
                {
                    var origins = None(width);
                    foreach (var partition in rel.PartitionedScan.Partitions)
                    {
                        var branch = Of(partition, path);
                        for (var i = 0; i < width && i < branch.Count; i++)
                        {
                            origins[i].UnionWith(branch[i]);
                        }
                    }

                    return origins;
                }

                case Rel.KindOneofCase.Window:
                {
                    var input = Of(rel.Window.Input, path);
                    var origins = None(width);
                    for (var i = 0; i < input.Count && i < width; i++)
                    {
                        origins[i].UnionWith(input[i]);
                    }

                    // The window's own outputs follow the input row, one per call, and each is a
                    // derived value of whatever its arguments read.
                    for (var c = 0; c < rel.Window.Calls.Count; c++)
                    {
                        var at = input.Count + c;
                        if (at >= width)
                        {
                            break;
                        }

                        foreach (var argument in rel.Window.Calls[c].Args)
                        {
                            Derive(origins[at], input, argument);
                        }
                    }

                    return origins;
                }

                case Rel.KindOneofCase.Hop:
                case Rel.KindOneofCase.Session:
                case Rel.KindOneofCase.Unnest:
                {
                    // Each widens its input's row with columns of its own; the input's columns keep
                    // their origins and the added ones have none.
                    var input = Of(Input(rel), path);
                    var origins = None(width);
                    for (var i = 0; i < input.Count && i < width; i++)
                    {
                        origins[i].UnionWith(input[i]);
                    }

                    return origins;
                }

                default:
                    // Unreachable: `Traced` is checked above and is the list this switch mirrors.
                    return None(width);
            }
        }

        private static Rel Input(Rel rel) => rel.KindCase switch
        {
            Rel.KindOneofCase.Hop => rel.Hop.Input,
            Rel.KindOneofCase.Session => rel.Session.Input,
            _ => rel.Unnest.Input,
        };

        /// <summary>An adaptive join's lookup alternative, as a node the walk can be asked about.</summary>
        private static Rel Wrap(LookupJoin join) =>
            new() { LookupJoin = join, RowType = RowTypeOf(join) };

        private static RowType RowTypeOf(LookupJoin join)
        {
            var row = new RowType();
            row.Fields.AddRange(join.Driving.RowType.Fields);
            if (join.Type is not (JoinType.Semi or JoinType.Anti))
            {
                row.Fields.AddRange(join.Lookup.RowType.Fields);
            }

            return row;
        }

        private IReadOnlyList<HashSet<Origin>> Leaf(Read read, IReadOnlyList<uint> projection, int width)
        {
            var origins = None(width);
            if (read.Disclosures.Count == 0 && !read.Correlation)
            {
                return origins;
            }

            for (var i = 0; i < width && i < projection.Count; i++)
            {
                origins[i].Add(
                    new Origin(
                        read, (int)projection[i], Derived: false, Correlation: read.Correlation));
            }

            return origins;
        }

        private IReadOnlyList<HashSet<Origin>> Aggregated(Aggregate aggregate, string path, int width)
        {
            var input = Of(aggregate.Input, path);
            var origins = None(width);
            var at = 0;

            // Keys then measures (I-IR-7). A key is the input column itself — which is why the
            // planner ships its own column-origin handler for Aggregate rather than Calcite's, whose
            // index is off by the group set (CALCITE-4250) — and a measure is derived from its
            // arguments.
            foreach (var grouping in aggregate.Groupings)
            {
                foreach (var key in grouping.Keys)
                {
                    if (at >= width)
                    {
                        break;
                    }

                    if (key < input.Count)
                    {
                        origins[at].UnionWith(input[(int)key]);
                    }

                    at++;
                }
            }

            foreach (var measure in aggregate.Measures)
            {
                if (at >= width)
                {
                    break;
                }

                // A measure is a value of its *arguments*. Its FILTER decides which rows are
                // counted rather than what the count is made of, and the report judges an aggregate
                // the same way — which is why COUNT(*) FILTER (WHERE …) is a count and not a
                // disclosure of what it filtered on.
                foreach (var argument in measure.Args)
                {
                    Derive(origins[at], input, argument);
                }

                at++;
            }

            return origins;
        }

        private IReadOnlyList<HashSet<Origin>> Joined(
            Rel left, Rel right, JoinType type, string path, int width)
        {
            var origins = None(width);
            var l = Of(left, path);
            for (var i = 0; i < l.Count && i < width; i++)
            {
                origins[i].UnionWith(l[i]);
            }

            if (type is JoinType.Semi or JoinType.Anti)
            {
                return origins;
            }

            var r = Of(right, path);
            for (var i = 0; i < r.Count; i++)
            {
                var at = l.Count + i;
                if (at >= width)
                {
                    break;
                }

                origins[at].UnionWith(r[i]);
            }

            return origins;
        }

        private static IReadOnlyList<HashSet<Origin>> Projected(
            IReadOnlyList<HashSet<Origin>> input, IReadOnlyList<Expr> exprs, int width)
        {
            var origins = None(width);
            for (var i = 0; i < exprs.Count && i < width; i++)
            {
                var expr = exprs[i];
                if (expr.KindCase == Expr.KindOneofCase.FieldRef
                    && expr.FieldRef.Index < input.Count)
                {
                    origins[i].UnionWith(input[(int)expr.FieldRef.Index]);
                    continue;
                }

                Derive(origins[i], input, expr);
            }

            return origins;
        }

        /// <summary>
        /// Every entitled column {@code expr} reads <em>as a value</em>, marked derived: the output
        /// is a value of them.
        /// </summary>
        /// <remarks>
        /// A <c>CASE</c> is the one expression whose operands are not all values of it. Its result
        /// arms are what the output can be; its conditions decide <em>which</em> arm, and are a
        /// predicate position — no more an origin of the value than a <c>Filter</c>'s condition or
        /// an aggregate's <c>FILTER</c> is, both of which this walk already passes over (F81, ADR
        /// 0058 §2). Reading them as origins is what labelled a sanitised column by the columns its
        /// own rule conditions consult. The conditions are not thereby unchecked: clause (b) holds
        /// them to what D203 permits in a predicate, which is where they now stand.
        /// </remarks>
        private static void Derive(
            HashSet<Origin> into, IReadOnlyList<HashSet<Origin>> input, Expr? expr)
        {
            if (expr is null)
            {
                return;
            }

            if (expr.KindCase == Expr.KindOneofCase.IfThen)
            {
                foreach (var clause in expr.IfThen.Clauses)
                {
                    Derive(into, input, clause.Result);
                }

                Derive(into, input, expr.IfThen.ElseBranch);
                return;
            }

            if (expr.KindCase == Expr.KindOneofCase.FieldRef)
            {
                var index = (int)expr.FieldRef.Index;
                if (index < input.Count)
                {
                    foreach (var origin in input[index])
                    {
                        into.Add(origin with { Derived = true });
                    }
                }

                return;
            }

            foreach (var child in PlanWalker.Children(expr))
            {
                Derive(into, input, child);
            }
        }

        /// <summary>Every input field index an expression reads.</summary>
        /// <summary>
        /// Whether every raw population-only value {@code expr} reads stands inside a comparison the
        /// column's own rules permit (D261, <c>docs/design/36-test-verdict.md</c> §2).
        /// </summary>
        /// <remarks>
        /// <c>=</c> and <c>&lt;&gt;</c> with a bare field reference on one side and, on the other, an
        /// operand that reads no column of the row; or an <c>IN</c> list of such operands, which is
        /// the third shape. A comparison that matches is not descended into — it is one value, the
        /// one bit the policy named — and everything else is walked to the references, where a raw
        /// population-only one is a use and is refused.
        /// </remarks>
        private bool ReadsRawOnlyInPermittedTests(IReadOnlyList<HashSet<Origin>> input, Expr? expr)
        {
            if (expr is null)
            {
                return true;
            }

            if (IsPermittedTest(input, expr))
            {
                return true;
            }

            if (expr.KindCase == Expr.KindOneofCase.FieldRef)
            {
                return !IsRawPopulationOnly(input, (int)expr.FieldRef.Index);
            }

            foreach (var child in PlanWalker.Children(expr))
            {
                if (!ReadsRawOnlyInPermittedTests(input, child))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>One comparison of a population-only column that its own rules permit (D261).</summary>
        private bool IsPermittedTest(IReadOnlyList<HashSet<Origin>> input, Expr expr)
        {
            if (expr.KindCase == Expr.KindOneofCase.InList)
            {
                if (expr.InList.Value.KindCase != Expr.KindOneofCase.FieldRef)
                {
                    return false;
                }

                foreach (var option in expr.InList.Options)
                {
                    if (ReadsAnyColumn(option))
                    {
                        return false;
                    }
                }

                return Permits(input, (int)expr.InList.Value.FieldRef.Index, "IN");
            }

            if (expr.KindCase != Expr.KindOneofCase.Call || expr.Call.Args.Count != 2)
            {
                return false;
            }

            var shape = expr.Call.Function switch
            {
                FunctionId.Eq => "EQUALS",
                FunctionId.Ne => "NOT_EQUALS",
                _ => null,
            };
            if (shape is null)
            {
                return false;
            }

            var left = expr.Call.Args[0];
            var right = expr.Call.Args[1];
            if (left.KindCase == Expr.KindOneofCase.FieldRef && !ReadsAnyColumn(right))
            {
                return Permits(input, (int)left.FieldRef.Index, shape);
            }

            return right.KindCase == Expr.KindOneofCase.FieldRef
                && !ReadsAnyColumn(left)
                && Permits(input, (int)right.FieldRef.Index, shape);
        }

        /// <summary>
        /// Whether column <paramref name="index"/> of the input is a raw population-only column whose
        /// every origin's read permits this comparison shape.
        /// </summary>
        private bool Permits(IReadOnlyList<HashSet<Origin>> input, int index, string shape)
        {
            if (index < 0 || index >= input.Count || input[index].Count == 0)
            {
                return false;
            }

            foreach (var origin in input[index])
            {
                if (origin.Derived || origin.Outcome != DisclosureOutcome.Aggregate)
                {
                    return false;
                }

                var permitted = false;
                foreach (var disclosure in origin.Read.Disclosures)
                {
                    if (disclosure.Column != origin.TableColumn)
                    {
                        continue;
                    }

                    foreach (var named in disclosure.TestShapes)
                    {
                        permitted |= string.Equals(named, shape, StringComparison.Ordinal);
                    }
                }

                if (!permitted)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether every reference to a raw population-only column in <paramref name="expr"/> stands
        /// where §3.5 puts one — as a whole arm of a sanitising <c>CASE</c> — and nowhere else.
        /// </summary>
        /// <remarks>
        /// The sanitiser of §3.2 read by shape rather than by position, and the stronger of the two
        /// readings: where the position rule accepts whatever a projection over the read holds, this
        /// one accepts the raw value only as a value being carried, never as something a condition
        /// selects rows by. A condition may read the column exactly where D261's allow-list already
        /// permits it, and an arm may be a bare reference, a numeric cast of one, or a nested
        /// sanitiser, which is what a mixed-rights principal's rule list folds to. It discloses
        /// nothing a bare reference does not — the clause already lets the value travel as a
        /// pass-through — and what it exists to catch is a <em>use</em>, which is what a condition
        /// reading the raw value would be and is refused here.
        /// </remarks>
        private bool IsSanitisedUse(IReadOnlyList<HashSet<Origin>> input, Expr? expr)
        {
            if (expr is null || !ReadsRawPopulationOnly(input, expr))
            {
                return true;
            }

            if (IsPassThrough(expr))
            {
                return true;
            }

            if (expr.KindCase != Expr.KindOneofCase.IfThen)
            {
                return false;
            }

            foreach (var clause in expr.IfThen.Clauses)
            {
                if (!ReadsRawOnlyInPermittedTests(input, clause.Condition)
                    || !IsSanitisedUse(input, clause.Result))
                {
                    return false;
                }
            }

            return IsSanitisedUse(input, expr.IfThen.ElseBranch);
        }

        /// <summary>Whether any reference in an expression reads a raw population-only column.</summary>
        private bool ReadsRawPopulationOnly(IReadOnlyList<HashSet<Origin>> input, Expr expr)
        {
            foreach (var index in Fields(expr))
            {
                if (IsRawPopulationOnly(input, index))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsRawPopulationOnly(IReadOnlyList<HashSet<Origin>> input, int index)
        {
            if (index < 0 || index >= input.Count)
            {
                return false;
            }

            foreach (var origin in input[index])
            {
                if (!origin.Derived && origin.Outcome == DisclosureOutcome.Aggregate)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ReadsAnyColumn(Expr? expr)
        {
            foreach (var _ in Fields(expr ?? new Expr()))
            {
                return true;
            }

            return false;
        }

        private static IEnumerable<int> Fields(Expr expr)
        {
            var found = new List<int>();
            Collect(expr, found);
            return found;
        }

        private static void Collect(Expr? expr, List<int> into)
        {
            if (expr is null)
            {
                return;
            }

            if (expr.KindCase == Expr.KindOneofCase.FieldRef)
            {
                into.Add((int)expr.FieldRef.Index);
                return;
            }

            foreach (var child in PlanWalker.Children(expr))
            {
                Collect(child, into);
            }
        }

        private static List<HashSet<Origin>> None(int width)
        {
            var origins = new List<HashSet<Origin>>(width);
            for (var i = 0; i < width; i++)
            {
                origins.Add([]);
            }

            return origins;
        }

        private static IReadOnlyList<HashSet<Origin>> Widen(
            IReadOnlyList<HashSet<Origin>> origins, int width)
        {
            if (origins.Count == width)
            {
                return origins;
            }

            var widened = None(width);
            for (var i = 0; i < origins.Count && i < width; i++)
            {
                widened[i].UnionWith(origins[i]);
            }

            return widened;
        }

        private static IReadOnlyList<HashSet<Origin>> Union(
            IReadOnlyList<HashSet<Origin>> left, IReadOnlyList<HashSet<Origin>> right, int width)
        {
            var origins = None(width);
            for (var i = 0; i < width; i++)
            {
                if (i < left.Count)
                {
                    origins[i].UnionWith(left[i]);
                }

                if (i < right.Count)
                {
                    origins[i].UnionWith(right[i]);
                }
            }

            return origins;
        }

        // ---------------------------------------------------------------- clause (b)

        /// <summary>
        /// Clause (b): every reference whose origin <em>is</em> a population-only column is an
        /// argument of a population aggregate — or, where the read says the column is statistical, a
        /// position D203 permits: a predicate, a join condition, an aggregate's FILTER, a grouping
        /// key.
        /// </summary>
        internal void Tainted(Rel? rel, string path)
        {
            if (rel is null)
            {
                return;
            }

            var here = $"{path}/{rel.KindCase}";
            foreach (var pushed in PlanWalker.Pushed(rel))
            {
                Tainted(pushed, here + "/pushed");
            }

            foreach (var input in PlanWalker.Inputs(rel))
            {
                Tainted(input, here);
            }

            switch (rel.KindCase)
            {
                case Rel.KindOneofCase.Aggregate:
                    Aggregates(rel.Aggregate, here);
                    break;
                case Rel.KindOneofCase.HashAggregate:
                    Aggregates(rel.HashAggregate.Aggregate, here);
                    break;
                case Rel.KindOneofCase.StreamAggregate:
                    Aggregates(rel.StreamAggregate.Aggregate, here);
                    break;
                case Rel.KindOneofCase.Project:
                {
                    // The projection sitting directly over an entitled read, through filters alone,
                    // is that leaf's own sanitiser — the one place in the plan that may read a raw
                    // column (§3.1), and where a population-only column's permitted branch *is* the
                    // column. Recognised by position, because a node the pass created has no identity
                    // left after optimisation.
                    if (LeafUnder(rel.Project.Input))
                    {
                        break;
                    }

                    var input = Of(rel.Project.Input, here);
                    foreach (var expr in rel.Project.Exprs)
                    {
                        if (IsPassThrough(expr))
                        {
                            continue;
                        }

                        // The one expression over a raw population-only column D261 permits: a
                        // comparison of a shape the read's own breadcrumbs say the column's rules
                        // name, whose other operands read no column of the row. It is the leaf's own
                        // — the comparison it computed instead of disclosing the value — and it is
                        // recognised here by its shape and by the read's list, never on the
                        // planner's word, which is what makes this walk a check of its own.
                        if (ReadsRawOnlyInPermittedTests(input, expr))
                        {
                            continue;
                        }

                        // The other expression §3.5 puts over a raw population-only column: its own
                        // sanitiser, recognised by shape where the position rule above cannot see
                        // it. A declared path puts the verdict its marker joins compute into
                        // projections of its own between the sanitiser and the read (D265), and the
                        // walk to the leaf goes through filters and nothing else — so the projection
                        // holding the sanitiser is no longer directly over the read (F86).
                        if (IsSanitisedUse(input, expr))
                        {
                            continue;
                        }

                        Refuse(input, expr, here, "an expression in a projection");
                    }

                    break;
                }

                case Rel.KindOneofCase.Sort:
                    foreach (var field in rel.Sort.Fields)
                    {
                        // A statistical column that has been through an Aggregate as a group key is
                        // a k-anonymous value by then, and ordering the result by it discloses
                        // nothing the key itself did not; ordering by the *raw* value below the
                        // aggregate is what the logical trace refuses, and does.
                        RefuseExpr(
                            Of(rel.Sort.Input, here), field.Expr, here, "a sort key",
                            statisticalAllowed: true);
                    }

                    break;

                case Rel.KindOneofCase.TopN:
                    foreach (var field in rel.TopN.Fields)
                    {
                        RefuseExpr(
                            Of(rel.TopN.Input, here), field.Expr, here, "a sort key",
                            statisticalAllowed: true);
                    }

                    break;

                case Rel.KindOneofCase.Window:
                    foreach (var call in rel.Window.Calls)
                    {
                        foreach (var argument in call.Args)
                        {
                            Refuse(
                                Of(rel.Window.Input, here), argument, here, "a window function's argument");
                        }
                    }

                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Whether an entitled read sits directly below — through the row filter, and through the
        /// joins the pass itself put in between.
        /// </summary>
        /// <remarks>
        /// Under execute-time binding each distinct membership over a bound list is computed once as
        /// a marker column, by a join to that list (§3.2, D209); under the split of F36 a leaf is
        /// anti-joined against the lists it does not satisfy. What makes such a join the pass's own
        /// rather than the statement's is that its right side reads bound relations and nothing else:
        /// a join to a catalog table stops the descent, so somebody else's projection is never
        /// mistaken for the leaf's sanitiser.
        /// </remarks>
        private static bool LeafUnder(Rel? rel)
        {
            var at = rel;
            while (at is not null)
            {
                if (at.KindCase == Rel.KindOneofCase.Filter)
                {
                    at = at.Filter.Input;
                    continue;
                }

                Rel[] inputs = [.. PlanWalker.Inputs(at)];
                if (inputs.Length == 2 && ReadsOnlyBoundTables(inputs[1]))
                {
                    at = inputs[0];
                    continue;
                }

                break;
            }

            return at is not null
                && at.KindCase == Rel.KindOneofCase.Read
                && at.Read.Disclosures.Count > 0;
        }

        /// <summary>Whether this subtree reads bound relations and no table of the catalog.</summary>
        private static bool ReadsOnlyBoundTables(Rel rel)
        {
            var bound = false;
            foreach (var node in PlanWalker.Rels(rel))
            {
                switch (node.KindCase)
                {
                    case Rel.KindOneofCase.BoundTable:
                        bound = true;
                        break;
                    case Rel.KindOneofCase.Read:
                    case Rel.KindOneofCase.IndexLookup:
                    case Rel.KindOneofCase.RemoteQuery:
                    case Rel.KindOneofCase.VirtualTable:
                    case Rel.KindOneofCase.PartitionedScan:
                        return false;
                    default:
                        break;
                }
            }

            return bound;
        }

        /// <summary>
        /// A bare reference, or a cast of one to a numeric type: a cast cannot select a row, so it is
        /// not a use and the value stays what it was (§3.4).
        /// </summary>
        private static bool IsPassThrough(Expr expr)
        {
            if (expr.KindCase == Expr.KindOneofCase.FieldRef)
            {
                return true;
            }

            return expr.KindCase == Expr.KindOneofCase.Cast
                && expr.Cast.Input.KindCase == Expr.KindOneofCase.FieldRef
                && expr.Type.Kind is TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64
                    or TypeKind.Fp32 or TypeKind.Fp64 or TypeKind.Decimal;
        }

        private void Aggregates(Aggregate aggregate, string path)
        {
            var input = Of(aggregate.Input, path);
            foreach (var grouping in aggregate.Groupings)
            {
                foreach (var key in grouping.Keys)
                {
                    RefuseColumn(input, (int)key, path, "a grouping key", statisticalAllowed: true);
                }
            }

            foreach (var measure in aggregate.Measures)
            {
                var population = measure.UserFunction.Length == 0 && Population.Contains(measure.Function);
                foreach (var argument in measure.Args)
                {
                    if (population && argument.KindCase == Expr.KindOneofCase.FieldRef)
                    {
                        continue;
                    }

                    Refuse(input, argument, path, "an aggregate that is not a population aggregate");
                }

                if (measure.Filter is { } filter)
                {
                    RefuseExpr(input, filter, path, "an aggregate's FILTER", statisticalAllowed: true);
                }
            }
        }

        private void Refuse(
            IReadOnlyList<HashSet<Origin>> input, Expr expr, string path, string use) =>
            RefuseExpr(input, expr, path, use, statisticalAllowed: false);

        /// <summary>
        /// Every reference {@code expr} makes, held to what this position permits — and a
        /// <c>CASE</c>'s conditions held to what a <em>predicate</em> permits instead, wherever the
        /// <c>CASE</c> stands (F81, ADR 0058 §2).
        /// </summary>
        /// <remarks>
        /// A condition selects which arm a row takes and is the same question D203 answers for an
        /// aggregate's <c>FILTER</c>, which decides which rows are counted: a statistical column may
        /// stand in one, a population-only column may not. Holding the two to the same rule is also
        /// what keeps <c>SUM(CASE WHEN c THEN x END)</c> and the <c>SUM(x) FILTER (WHERE c)</c> an
        /// optimiser may rewrite it into from being judged differently.
        /// </remarks>
        private void RefuseExpr(
            IReadOnlyList<HashSet<Origin>> input,
            Expr? expr,
            string path,
            string use,
            bool statisticalAllowed)
        {
            if (expr is null)
            {
                return;
            }

            if (expr.KindCase == Expr.KindOneofCase.IfThen)
            {
                foreach (var clause in expr.IfThen.Clauses)
                {
                    RefuseExpr(
                        input, clause.Condition, path, "a CASE condition", statisticalAllowed: true);
                    RefuseExpr(input, clause.Result, path, use, statisticalAllowed);
                }

                RefuseExpr(input, expr.IfThen.ElseBranch, path, use, statisticalAllowed);
                return;
            }

            if (expr.KindCase == Expr.KindOneofCase.FieldRef)
            {
                RefuseColumn(input, (int)expr.FieldRef.Index, path, use, statisticalAllowed);
                return;
            }

            foreach (var child in PlanWalker.Children(expr))
            {
                RefuseExpr(input, child, path, use, statisticalAllowed);
            }
        }

        private void RefuseColumn(
            IReadOnlyList<HashSet<Origin>> input,
            int index,
            string path,
            string use,
            bool statisticalAllowed)
        {
            if (index < 0 || index >= input.Count)
            {
                return;
            }

            foreach (var origin in input[index])
            {
                if (origin.Derived || origin.Outcome != DisclosureOutcome.Aggregate)
                {
                    continue;
                }

                if (statisticalAllowed && origin.Statistical)
                {
                    continue;
                }

                throw new InvalidPlanException(
                    Invariant,
                    path,
                    $"column {origin.TableColumn} of {Name(origin.Read.Table)} is population-only for "
                    + $"this principal and this plan reads it as {use}");
            }
        }
    }

    private static string Name(TableRef table) =>
        $"{table.SourceId}.{table.Schema}.{table.Table}";

    private static string Field(Plan plan, int index) =>
        index < plan.OutputType.Fields.Count ? plan.OutputType.Fields[index].Name : $"#{index}";

    /// <summary>Reference identity: two structurally equal nodes are still two nodes.</summary>
    private sealed class Same : IEqualityComparer<Rel>
    {
        internal static Same Instance { get; } = new();

        public bool Equals(Rel? left, Rel? right) => ReferenceEquals(left, right);

        public int GetHashCode(Rel value) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}
