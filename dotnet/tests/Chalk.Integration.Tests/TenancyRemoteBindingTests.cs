using System.Text;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The <c>m7-tenancy</c> corpus under execute-time and partial binding with the entitled tables in
/// a <b>database</b> (<c>docs/design/16-entitlements.md</c> §2, §2.1; D209, D232).
/// </summary>
/// <remarks>
/// <para>
/// Both families ran in process only, and that is where F55 hid: with an open half the entitled
/// leaf becomes a local subtree — the memberships are joins to context tables, which belong to no
/// source — and a POCO table has no boundary for that to matter at. Over a real source it matters
/// twice: the parent's visible keys have to reach the child's source somehow, and the joins above
/// the leaf have a remote input that cost must not put on a nested loop's inner side.
/// </para>
/// <para>
/// The assertion is the same one the in-process families make, one boundary further out: the rows a
/// principal gets from the database are the rows the same principal gets in process, under the same
/// binding. Saying it this way rather than with goldens of its own is deliberate — what is under
/// test is that a source boundary changes nothing about the policy, and a second set of goldens
/// could only disagree with the first.
/// </para>
/// <para>
/// Five of the corpus's thirty-six statements are not here, and none of them for a reason that
/// has anything to do with binding time — each was checked with everything <em>folded</em> and
/// disagrees there too, which is what says so. Three name tables this fixture does not hold
/// (<c>invites</c>, <c>notes</c>, <c>attachments</c>): it is §8's query-16 fixture, and adding them
/// would be a different fixture rather than a wider run. One reads <c>symbols</c>, which is in the
/// in-process schema and is not the one a statement names unqualified here. The five that aggregate
/// <c>orders.amount</c> were excluded as F57 — DuckDB's <c>SUM</c> over a <c>BIGINT</c> is a
/// <c>HUGEINT</c> — until the writer learned to cast an integer sum back to the type the plan
/// declares where the dialect widens it; they run here now. One is
/// <c>27_messages_join_threads</c>, whose generated SQL carries a table alias twice
/// (<c>FROM "messages" AS "t" AS "t2"</c>) when the statement's own join to the parent pushes
/// beside the pass's — a defect in the writer, registered as F56.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TenancyRemoteBindingTests(SharedSidecar sidecar)
{
    /// <summary>The tables this fixture does not hold; a statement naming one is not run here.</summary>
    private static readonly string[] Absent = ["invites", "notes", "attachments", "symbols"];

    /// <summary>
    /// The statements whose remote plan already disagrees with its in-process twin under
    /// prepare-time binding, so nothing about them is this suite's subject: the five that aggregate
    /// <c>orders.amount</c> (F57) and the one whose generated SQL aliases a table twice (F56).
    /// </summary>
    /// <remarks>
    /// <para>
    /// D265 clause (h) adds one more, registered rather than fixed (D251).
    /// </para>
    /// <para>
    /// <b>F82</b> — <c>09_lateral_count</c> — is fixed (ADR 0062 §3) and runs here in both binding
    /// modes like any other statement. Clause (h)'s rule on <c>orders.member_id</c> made the
    /// decorrelated count's join key a sanitiser and so nullable; the null-safe equality Calcite's
    /// decorrelator writes was then read as one the hash join may not take, although
    /// <c>members.id</c> on the other side is NOT NULL and the two readings cannot differ. The
    /// nested loop that was left put the source on its inner side, which M4 refuses by name.
    /// </para>
    /// <para>
    /// <b>F83</b> named two more — <c>41_orders_in_over_the_lines</c> and
    /// <c>42_orders_any_over_the_lines</c>, which with the tenancy folded returned <em>every</em>
    /// order to the global grant where the statement's own membership admits four. It was F72
    /// exactly, and both are fixed (ADR 0059): a pushed semi-join's reference back to the outer row
    /// was captured by a column of the same name inside the generated <c>EXISTS</c>. They run here
    /// in both binding modes like any other statement.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> NotAboutBindingTime =
        new(StringComparer.Ordinal)
        {
            "27_messages_join_threads",
        };

    /// <summary>
    /// The adversarial statements whose remote run already disagrees with its in-process twin for a
    /// registered reason, so nothing about them is this suite's subject. One aggregates
    /// <c>orders.amount</c>, which was F57 exactly as it was for the five <c>m7-tenancy</c>
    /// statements above: DuckDB's <c>SUM</c> over a <c>BIGINT</c> is a <c>HUGEINT</c>, which the
    /// scan contract refuses where the catalog declared <c>I64</c>. The other is F59's own
    /// statement, whose remote plan meets the non-constant separator for the global grant where the
    /// in-process plan does not — the same defect at a second locus, not a second one.
    /// </summary>
    /// <remarks>
    /// <b>F72</b> was a third, found when F61's statement 23 was un-excluded (ADR 0050, and the
    /// un-exclusion of 2026-09-16): over DuckDB with the tenancy folded, the correlated <c>IN</c>
    /// over two occurrences of <c>members</c> returned the <em>whole</em> outer table to <c>u4</c>,
    /// the global grant, where the statement's own membership admits three rows. It is fixed
    /// (ADR 0059) — the pushed semi-join's reference back to the outer row was captured by the
    /// sub-query's own <c>first_name</c> — and statement 23 runs here at every pushdown level, in
    /// both binding modes, like any other.
    /// </remarks>
    private static readonly HashSet<string> AdversarialNotAboutBindingTime =
        new(StringComparer.Ordinal)
        {
            "14_probe_string_agg",
            "40_known_limit_differencing_on_the_guard",
        };

    /// <summary>
    /// The adversarial statement whose shape this planner refuses over a remote source whatever the
    /// policy says: a <c>CROSS JOIN</c> has no equality for a lookup join, and a remote query on a
    /// nested loop's inner side is not supported (M4). It is the statement's own shape rather than
    /// anything about binding time or the rewrite, and the statement does its work in process.
    /// </summary>
    private static readonly HashSet<string> NotARemoteShape =
        new(StringComparer.Ordinal)
        {
            "37_two_aliases_with_different_predicates",
        };

    /// <summary>Every pushdown level the database runs of the adversarial family take (D251).</summary>
    private static readonly PushdownLevel[] Levels =
    [
        PushdownLevel.Full,
        PushdownLevel.FiltersOnly,
        PushdownLevel.ProjectionOnly,
        PushdownLevel.None,
    ];

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7Tenancy())
        {
            if (!NotAboutBindingTime.Contains(query.Name)
                && !Absent.Any(t => query.Sql.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    /// <summary>
    /// The adversarial family over a database, at every pushdown level (D251): what a statement
    /// learns must not depend on how much of it the source evaluated, so the level is part of the
    /// case rather than a default.
    /// </summary>
    public static TheoryData<string, PushdownLevel> AdversarialQueries()
    {
        var data = new TheoryData<string, PushdownLevel>();
        foreach (var query in CorpusQueries.LoadM7Adversarial())
        {
            if (TenancyCorpusTests.NotPlanned.ContainsKey(query.Name)
                || AdversarialNotAboutBindingTime.Contains(query.Name)
                || NotARemoteShape.Contains(query.Name)
                || Absent.Any(t => query.Sql.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var level in Levels)
            {
                data.Add(query.Name, level);
            }
        }

        return data;
    }

    /// <summary>
    /// Execute-time binding (D209) over a database: one plan for every principal of the shape, the
    /// values bound when it runs, and the rows the in-process shared plan gave.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public Task Every_principal_sees_over_a_database_what_they_saw_in_process(string name) =>
        AgreeAsync(name, partial: false);

    /// <summary>
    /// And partial binding (D232): the tenancy folded into the leaf — which over a database is what
    /// puts the predicate in the source's own SQL — and the subject left open beside it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public Task Every_principal_sees_the_same_with_the_tenancy_folded(string name) =>
        AgreeAsync(name, partial: true);

    /// <summary>The adversarial battery over a database, at every pushdown level (D251).</summary>
    [Theory]
    [MemberData(nameof(AdversarialQueries))]
    public Task Every_subversion_over_a_database_learns_what_it_learned_in_process(
        string name, PushdownLevel pushdown) =>
        AgreeAsync(name, partial: false, pushdown, adversarial: true);

    /// <summary>And the same with the tenancy folded into the leaf.</summary>
    [Theory]
    [MemberData(nameof(AdversarialQueries))]
    public Task Every_subversion_learns_the_same_with_the_tenancy_folded(
        string name, PushdownLevel pushdown) =>
        AgreeAsync(name, partial: true, pushdown, adversarial: true);

    // ------------------------------------------------- the two-source case (D265 clause (h))

    /// <summary>
    /// Design 38 §5 and §8's own two-source layout, which is the tutorial's (design 39 §4): the
    /// <b>target and the bridge</b> co-located in DuckDB and <c>items</c> and <c>vendors</c> in
    /// process. The endpoint's keys that satisfy its predicate reach the bridge's source as a key
    /// set, and the semi-join back to the target goes with them.
    /// </summary>
    /// <remarks>
    /// Asserted three ways, because each says something the others do not. The <b>rows</b> are the
    /// rows the same principal gets with all three tables in one source, which is what says a source
    /// boundary changes nothing about the policy. The remote <b>text</b> holds the bridge and not
    /// the endpoint — <c>items</c> is not in that source to be joined to — and carries the key set
    /// as a bound list. And the <b>report</b> says the target is SOME along the path, never ALL:
    /// existence is per row (§6).
    /// </remarks>
    [Fact]
    public async Task The_endpoint_in_another_source_is_refused_at_registration()
    {
        // F84. §5 describes the two-source case as a two-hop key-set exchange — the endpoint's keys
        // travel to the bridge's source, and `K`'s keys travel on to the target's — and §3 requires
        // a **declared foreign key** for every step of a path. A foreign key names a table of its
        // own schema (`catalog.proto`), so there is no declaration a path across two sources can be
        // written with, and registration refuses it by name. It fails **closed**, and the exchange
        // §5 describes has no way in until a later decision gives a step something other than a
        // foreign key to resolve through. Registered and not built in the run that found it (D251).
        using var split = TenancyAdoFixture.CreateDuckDb(
            marketplace: MarketplacePlacement.EndpointInProcess);
        var refusal = await Assert.ThrowsAsync<CatalogValidationException>(
            async () => await EngineAsync(split.Sources));

        Assert.Contains(
            "goes down from 'order_items' to 'items'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(
            "declares no foreign key naming 'items.id'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(
            "The direction is checked and never inferred", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the one-source layout does: the target, the bridge and the endpoint in one database,
    /// and the same rows a principal sees in process (§5).
    /// </summary>
    /// <remarks>
    /// How much of the key set leaves the process is the costing's and the dialect profile's, which
    /// is what §5 says of it — "the marker pushes as one remote query when the dialect profile
    /// admits the shape … where it does not, <c>K</c> is computed where it can be and reaches the
    /// target's source as a key set". This run's plan for DuckDB reads the target's own columns
    /// remotely and computes the chain above them; what the test holds is the answer, which is the
    /// one thing that may not depend on where the work happened.
    /// </remarks>
    [Fact]
    public async Task The_co_located_chain_answers_what_the_same_principal_sees_in_process()
    {
        using var together = TenancyAdoFixture.CreateDuckDb();

        const string Sql = "SELECT id FROM orders ORDER BY id";
        var vendor = TenancyFixture.U14;

        await using var engine = await EngineAsync(together.Sources);
        var prepared = await engine.WithEntitlements().PrepareAsync(Sql, vendor);

        var orders = Assert.Single(prepared.Entitlements.Tables, t => t.Table == "orders");
        Assert.Equal(TableVisibility.Some, orders.Visibility);

        // Orders 1, 5 and 7 — the ones some line of which carries an item of vendor 1, order 7 in
        // an organisation no other grant of the fixture reaches, which is what makes "a row visible
        // through the rows that belong to it" an assertion rather than a coincidence.
        Assert.Equal(["1", "5", "7"], await RowsOfAsync(engine, prepared));
    }

    /// <summary>Every remote query text in the plan, joined — there may be more than one here.</summary>
    private static string RemoteTextOf(Plan plan) =>
        string.Join(
            "\n",
            PlanWalker.ExecutedRels(plan)
                .Where(r => r.KindCase == Rel.KindOneofCase.RemoteQuery)
                .Select(r => r.RemoteQuery.QueryText));

    private static async Task<string[]> RowsOfAsync(ChalkEngine engine, EntitledQuery prepared)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared.Query);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? ""))));
            }
        }

        return [.. rows];
    }

    /// <summary>
    /// One transcript per engine — principal, then the rows or the refusal — compared as a whole, so
    /// a disagreement names the principal it is about rather than only the statement.
    /// </summary>
    private async Task AgreeAsync(
        string name, bool partial, PushdownLevel? pushdown = null, bool adversarial = false)
    {
        var query = (adversarial ? CorpusQueries.LoadM7Adversarial() : CorpusQueries.LoadM7Tenancy())
            .Single(q => q.Name == name);

        await using var remote = await EngineAsync(TenancyAdoFixture.Shared.Sources);
        await using var local = await EngineAsync(
            [(adversarial ? TenancyFixture.Subversion : TenancyFixture.Shared).Source]);

        var where = partial ? "folded" : "execute-time";
        var here = new StringBuilder();
        var there = new StringBuilder();
        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            var detector = LeakDetector.For(principal, context);
            var bound = partial ? TenancyFixture.PartiallyBound(context) : context.Shape();
            Record(
                here,
                principal,
                await RowsAsync(
                    local, query, bound, context, detector, $"{name} (in process, {where})", null));
            Record(
                there,
                principal,
                await RowsAsync(
                    remote,
                    query,
                    bound,
                    context,
                    detector,
                    $"{name} (over a database, {where}, pushdown {pushdown?.ToString() ?? "Full"})",
                    pushdown));
        }

        Assert.Equal(here.ToString(), there.ToString());
    }

    /// <summary>
    /// Two occurrences of one table, disclosing differently, both <b>pushed into the source</b>
    /// (F95, ADR 0062 §5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape is <c>m7-adversarial</c> 37's, with an equality so that it is a remote shape: one
    /// organisation's names beside another's, from one table, under two aliases. Each leaf's rules
    /// are folded under its own filter, so for a principal who manages the first organisation and
    /// acts as an agent in the second one occurrence discloses <c>first_name</c> in full and the
    /// other masks it.
    /// </para>
    /// <para>
    /// What is asserted is the same thing the corpus asserts of every statement — the database
    /// answers what the same principal sees in process, as every principal, with D252's detector
    /// over the rows, the report and the SQL each source is sent — and, for the mixed principal, the
    /// thing a row cannot show: the two pushed reads carry their <em>own</em> disclosures. Before the
    /// fix they carried one occurrence's for both, because a queryable table's entitled leaf starts
    /// as a <c>LogicalTableScan</c> whose digest has nowhere to put the map, so the two unified and
    /// one pushed scan served both parents. The rows were right; the breadcrumbs were not, in the
    /// permissive direction.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_pushed_occurrences_of_one_table_keep_their_own_disclosures()
    {
        const string Sql =
            "SELECT a.first_name AS in_o1, b.first_name AS in_o2"
            + " FROM (SELECT org_id, first_name FROM members WHERE org_id = 1) a"
            + " JOIN (SELECT org_id, first_name FROM members WHERE org_id = 2) b"
            + " ON b.org_id = a.org_id + 1"
            + " ORDER BY in_o1, in_o2";

        await using var remote = await EngineAsync(TenancyAdoFixture.Shared.Sources);
        await using var local = await EngineAsync([TenancyFixture.Shared.Source]);

        var here = new StringBuilder();
        var there = new StringBuilder();
        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            var detector = LeakDetector.For(principal, context);
            Record(here, principal, await TwoAliasesAsync(local, Sql, context, detector, "in process"));
            Record(
                there,
                principal,
                await TwoAliasesAsync(remote, Sql, context, detector, "over a database"));
        }

        Assert.Equal(here.ToString(), there.ToString());

        // The mixed principal: O1 as a manager, O2 as an agent. Two reads of `members`, two maps.
        var prepared = await remote.WithEntitlements().PrepareAsync(Sql, TenancyFixture.U1);
        var members = PlanWalker.Rels(prepared.Query.Plan)
            .Where(r => r.KindCase == Rel.KindOneofCase.Read && r.Read.Table.Table == "members")
            .Select(r => string.Join(
                ",", r.Read.Disclosures.Select(d => $"{d.Column}:{d.Outcome}")))
            .ToList();

        Assert.Equal(2, members.Count);
        Assert.NotEqual(members[0], members[1]);
        Assert.Contains(members, m => m.Contains("2:Full", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("2:Masked", StringComparison.Ordinal));
    }

    /// <summary>One engine's answer to the two-alias statement, with the detector over it.</summary>
    private static async Task<(string[] Rows, string Refusal)> TwoAliasesAsync(
        ChalkEngine engine,
        string sql,
        RequestContext context,
        LeakDetector detector,
        string where)
    {
        try
        {
            var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
            var rows = new List<string>();
            await using (var execution = await engine.ExecuteAsync(prepared.Query))
            {
                await foreach (var batch in execution.Batches)
                {
                    using (batch)
                    {
                        rows.AddRange(BatchReader.ToRows(batch).Select(
                            r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
                    }
                }
            }

            detector.Inspect(new LeakScan
            {
                Statement = $"two aliases, different predicates ({where})",
                Rows = rows,
                Columns = [.. prepared.Columns.Select(c => c.Name)],
                Report = string.Join(
                    ", ",
                    prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                        .Concat(prepared.Entitlements.Tables.Select(
                            t => $"{t.Table}:{t.Visibility}"))),
                RemoteSql = RemoteSql(prepared.Query.Plan),
            });
            return ([.. rows], "");
        }
        catch (EntitlementException refused)
        {
            detector.Inspect(new LeakScan
            {
                Statement = $"two aliases, different predicates ({where})",
                Refusal = refused.Message,
            });
            return ([], refused.Message);
        }
    }

    private static void Record(StringBuilder into, string principal, (string[] Rows, string Refusal) run)
    {
        into.Append("-- ").AppendLine(principal);
        if (run.Refusal.Length > 0)
        {
            // The first sentence alone: a refusal names the table and the reason, and the rest of
            // the message is guidance that mentions where the table lives.
            into.Append("POLICY ").AppendLine(Normalised(run.Refusal.Split(". ")[0]));
            return;
        }

        foreach (var row in run.Rows)
        {
            into.AppendLine(row);
        }
    }

    private async ValueTask<ChalkEngine> EngineAsync(IReadOnlyList<ISourceRuntime> sources) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = sources,
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    /// <summary>
    /// The rows this binding gives, or the refusal it is instead — with D252's detector over
    /// everything the principal received: the rows, the report, the refusal, the SQL the plan sends
    /// to a source, and the statistics record a host may log.
    /// </summary>
    private static async Task<(string[] Rows, string Refusal)> RowsAsync(
        ChalkEngine engine,
        CorpusQuery query,
        RequestContext prepareWith,
        RequestContext executeWith,
        LeakDetector detector,
        string statement,
        PushdownLevel? pushdown)
    {
        try
        {
            var prepared = await engine
                .WithEntitlements()
                .PrepareAsync(
                    query.Sql,
                    prepareWith,
                    query.PrepareOptions(pushdown ?? PushdownLevel.Full));
            var rows = new List<string>();
            string statistics;
            await using (var execution = await engine.ExecuteAsync(
                prepared.Query, executeWith, CorpusQueries.Parameters(query.Name)))
            {
                await foreach (var batch in execution.Batches)
                {
                    using (batch)
                    {
                        rows.AddRange(BatchReader.ToRows(batch).Select(
                            r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
                    }
                }

                statistics = LeakDetector.Statistics(execution.Stats);
            }

            detector.Inspect(new LeakScan
            {
                Statement = statement,
                Rows = rows,
                Columns = [.. prepared.Columns.Select(c => c.Name)],
                Report = string.Join(
                    ", ",
                    prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                        .Concat(prepared.Entitlements.Tables.Select(t => $"{t.Table}:{t.Visibility}"))),
                RemoteSql = RemoteSql(prepared.Plan),
                Statistics = statistics,
            });
            return ([.. rows], "");
        }
        catch (EntitlementException refused)
        {
            detector.Inspect(new LeakScan { Statement = statement, Refusal = refused.Message });
            return ([], refused.Message);
        }
        catch (Exception failed)
            when (TenancyCorpusTests.KnownLeaks.ContainsKey(query.Name)
                && TenancyCorpusTests.IsRegistered(failed))
        {
            // A registered defect (D251): both engines are expected to fail the same way, and the
            // detector reads the message either way. The type and the first sentence alone, because
            // a planner error names nodes and two plans do not have the same node numbers.
            detector.Inspect(new LeakScan { Statement = statement, Refusal = failed.Message });
            return ([], failed.GetType().Name + ": " + failed.Message);
        }
    }

    /// <summary>
    /// A refusal with the schema qualifier taken out. The two fixtures hold the same tables in
    /// different schemas on purpose — the remote source comes first so a statement names
    /// <c>members</c> unqualified on both — so the qualifier is the one word in a refusal that has
    /// to differ, and it is the only one normalised away. The table, the column and the reason are
    /// compared as they are written.
    /// </summary>
    private static string Normalised(string refusal) =>
        refusal
            .Replace(TenancyAdoFixture.SourceId + ".", "<schema>.", StringComparison.Ordinal)
            .Replace("main.", "<schema>.", StringComparison.Ordinal);

    /// <summary>Every remote query text the plan carries — the SQL a source is actually sent.</summary>
    internal static IReadOnlyList<string> RemoteSql(Plan plan)
    {
        var texts = new List<string>();
        foreach (var rel in PlanWalker.Rels(plan.Root))
        {
            if (rel.KindCase == Rel.KindOneofCase.RemoteQuery)
            {
                texts.Add(rel.RemoteQuery.QueryText);
            }
        }

        return texts;
    }
}
