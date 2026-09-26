using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Ir;
using Chalk.TestKit;
using AdoCapabilities = Chalk.Sources.Ado.AdoCapabilities;
using Enforcement = Chalk.Entitlements.Enforcement;
using SourceCapabilities = Chalk.Catalog.SourceCapabilities;

namespace Chalk.Integration.Tests;

/// <summary>
/// §8's query 16 and the classes that need a real source: the tenancy fixture with
/// <c>members</c> and <c>orders</c> in a database, reached through the ADO.NET source
/// (<c>docs/design/16-entitlements.md</c> §3.7, §3.8, §8).
/// </summary>
/// <remarks>
/// <para>
/// The rows are compared with the in-process fixture's, which is the only comparison worth making:
/// a policy that gives different answers depending on where the table lives is not a policy. What
/// is asserted on top of the rows is what only a remote table can show — the folded tenant set
/// leaving the process, the mask not leaving it, and the enforcement locus doing what it says.
/// </para>
/// <para>
/// DuckDB always. PostgreSQL when a server is there, and compulsorily when
/// <c>CHALK_TEST_POSTGRES_REQUIRED</c> is set: the SQL is generated for a dialect nobody wrote by
/// hand, and a second real one is what tells a Chalk-ism from a DuckDB-ism.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class EntitlementsAdoTests(SharedSidecar sidecar, SharedPostgres postgres) : IDisposable
{
    private readonly List<TenancyAdoFixture> _fixtures = [];

    // ---------------------------------------------------------------- 16: the same answers

    /// <summary>
    /// The heart of it: every principal sees exactly what they saw in process. The fixtures share
    /// their rows and their descriptors, so a difference here is one the remote path introduced.
    /// </summary>
    [Theory]
    [InlineData("SELECT id, first_name FROM members ORDER BY id")]
    [InlineData("SELECT id, national_id FROM members ORDER BY id")]
    [InlineData("SELECT id, org_id, last_name FROM members WHERE org_id = 2 ORDER BY id")]
    [InlineData("SELECT id, note FROM orders ORDER BY id")]
    [InlineData("SELECT COUNT(*) FROM members")]
    public async Task Q16_a_remote_table_discloses_exactly_what_the_local_one_did(string sql)
    {
        foreach (var (name, context) in TenancyFixture.Principals)
        {
            // A refusal is an answer too, and the two paths must agree on it: since D261 a counting
            // principal reads `national_id` as population-only and a value use of it is refused
            // (§3.4), here and there alike.
            var local = await OrRefusalAsync(() => PocoRowsAsync(sql, context));
            var remote = await OrRefusalAsync(() => RemoteRowsAsync(Duck(), sql, context));
            Assert.Equal(local, remote);
            Assert.NotNull(name);
        }
    }

    /// <summary>
    /// The tenant set leaves the process as an IN list in the source's own dialect, and the report
    /// says the row predicate was pushed — which run 2 could only ever report honestly false.
    /// </summary>
    [Fact]
    public async Task Q16_the_tenant_set_is_pushed_as_an_in_list_and_the_report_says_so()
    {
        var (prepared, remote) = await PlanAsync(
            Duck(), "SELECT id FROM members ORDER BY id", TwoOrgManager);

        var members = Assert.Single(prepared.Entitlements.Tables, t => t.Table == "members");
        Assert.True(members.RowPredicatePushed);
        Assert.Equal(TableVisibility.Some, members.Visibility);
        Assert.Contains("IN (1, 2)", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("*", remote, StringComparison.Ordinal);
    }

    /// <summary>Masks are computed here. Nothing of them is in the query the source runs.</summary>
    [Fact]
    public async Task Q16_the_mask_is_computed_locally_and_is_not_in_the_remote_text()
    {
        var (prepared, remote) = await PlanAsync(
            Duck(), "SELECT id, first_name FROM members ORDER BY id", TenancyFixture.U2);

        Assert.Equal(ReportedDisclosure.Masked, prepared.Columns[1].Disclosure);
        Assert.DoesNotContain("SUBSTRING", remote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("org_id", remote, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mixed filter of §8's query 16: the tenancy conjunct and the postcode go to the source,
    /// and the client-bodied <c>is_vip</c> stays here — the whole reason the filter-residual rule
    /// exists, since before it one unpushable conjunct held the tenancy predicate back with it.
    /// </summary>
    [Fact]
    public async Task Q16_a_mixed_filter_pushes_the_tenancy_conjunct_and_keeps_the_residual_local()
    {
        const string Sql =
            "SELECT id FROM members WHERE postcode = '2000' AND is_vip(id) ORDER BY id";
        var (prepared, remote) = await PlanAsync(Duck(), Sql, TwoOrgManager);

        Assert.Contains("IN (1, 2)", remote, StringComparison.Ordinal);
        Assert.Contains("'2000'", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("is_vip", remote, StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.Single(prepared.Entitlements.Tables, t => t.Table == "members").RowPredicatePushed);

        // Member 1 is in O1 with postcode 2000 and an odd id, so it is the one row that survives
        // both halves; member 3's postcode is 3000 and member 2's id is even.
        Assert.Equal(["1"], await RemoteRowsAsync(Duck(), Sql, TwoOrgManager));
    }

    /// <summary>
    /// An entitled query explains itself under the options it was prepared with (F142): a pair rule
    /// that keeps a wide membership's key set home is seen by the prepared report and by the query's
    /// own explanation alike, where before the explanation re-planned under no options and disagreed.
    /// </summary>
    [Fact]
    public async Task An_entitled_query_explains_under_the_options_it_was_prepared_with()
    {
        var fixture = Track(TenancyAdoFixture.CreateDuckDb());
        await using var engine = await RemoteEngineAsync(fixture);
        var wide = TenancyFixture.Principal(
            user: 1, managerOrgs: Enumerable.Range(1, 70).ToArray(), agentOrgs: [], auditorOrgs: [], subjectPairs: []);
        var nothingIntoTheSource = new PrepareOptions
        {
            JoinPolicy = new Chalk.Catalog.CrossSourceJoinPolicy
            {
                Pairs =
                [
                    new Chalk.Catalog.SourcePairRule
                    {
                        LeftSource = "",
                        RightSource = TenancyAdoFixture.SourceId,
                        Allowed = [Chalk.Catalog.JoinStrategy.Local],
                    },
                ],
            },
        };

        var kept = await engine.WithEntitlements().PrepareAsync("SELECT id FROM members", wide, nothingIntoTheSource);
        var explained = await kept.ExplainAsync();

        Assert.False(Assert.Single(kept.Entitlements.Tables, t => t.Table == "members").RowPredicatePushed);
        Assert.False(Assert.Single(explained.Tables, t => t.Table == "members").RowPredicatePushed);
    }

    // ---------------------------------------------------------------- the options class (§6, §8)

    /// <summary>
    /// The two enforcement loci agree on the rows and differ on what leaves the process: under
    /// <c>LOCAL</c> no predicate of the table is pushed at all, so the tenant set never appears in
    /// another system's query log.
    /// </summary>
    [Fact]
    public async Task Local_and_pushdown_agree_on_the_rows_and_differ_on_the_query()
    {
        const string Sql = "SELECT id, first_name FROM members ORDER BY id";
        var pushdown = Duck();
        var local = Track(TenancyAdoFixture.CreateDuckDb(Enforcement.Local));

        Assert.Equal(
            await RemoteRowsAsync(pushdown, Sql, TwoOrgManager),
            await RemoteRowsAsync(local, Sql, TwoOrgManager));

        var (report, text) = await PlanAsync(local, Sql, TwoOrgManager);
        Assert.False(Assert.Single(report.Entitlements.Tables, t => t.Table == "members").RowPredicatePushed);
        Assert.DoesNotContain("WHERE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IN (1, 2)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>trust_source_row_level_security</c> (D156) skips the row predicate for this source's tables and
    /// <b>nothing else</b>. This fixture's database has no row security of its own, so trusting it
    /// returns every row — which is exactly the point of the setting being a statement about the
    /// database that Chalk cannot check — while the column disclosures are still Chalk's.
    /// </summary>
    /// <remarks>
    /// And Chalk's disclosures are what they are for <em>each</em> row (F44). u2 is an agent in O1,
    /// so members 1 and 2 are inside the scope and take the agent's initial mask; every other row
    /// the database handed back is outside it, no rule matches, and the value is the placeholder.
    /// Before F44 the pass simplified the rules under a row predicate it does not emit for a trusted
    /// source, so the agent's rule folded to a constant and rows 3 to 7 came back as initials of
    /// names this principal holds no grant for.
    /// </remarks>
    [Fact]
    public async Task A_trusted_source_keeps_the_disclosures_and_loses_the_row_predicate()
    {
        var trusted = Track(TenancyAdoFixture.CreateDuckDb(trustSourceRowLevelSecurity: true));
        const string Sql = "SELECT id, first_name FROM members ORDER BY id";

        // Every row, because nothing filtered them — the agent's mask inside their scope and the
        // placeholder outside it.
        Assert.Equal(
            ["1|T", "2|B", "3|", "4|", "5|", "6|", "7|"],
            await RemoteRowsAsync(trusted, Sql, TenancyFixture.U2));

        var (report, text) = await PlanAsync(trusted, Sql, TenancyFixture.U2);
        Assert.False(Assert.Single(report.Entitlements.Tables, t => t.Table == "members").RowPredicatePushed);
        Assert.DoesNotContain("WHERE", text, StringComparison.Ordinal);

        // The label follows the values: one origin that is masked inside the scope and redacted
        // outside it is PerRow, and the visibility is still what this principal's grants come to.
        var prepared = report;
        Assert.Equal(
            ReportedDisclosure.PerRow,
            Assert.Single(prepared.Columns, c => c.Name == "first_name").Disclosure);
        Assert.Equal(
            TableVisibility.Some,
            Assert.Single(prepared.Entitlements.Tables, t => t.Table == "members").Visibility);
    }

    /// <summary>
    /// §8's query 21: <c>PUSHDOWN_REQUIRED</c> over a source that does not declare the IN shape.
    /// Under the default the predicate is honestly evaluated locally; under the constraint the plan
    /// is refused, because for a source holding every tenancy's rows a silent full fetch is worse.
    /// </summary>
    [Fact]
    public async Task Q21_pushdown_required_refuses_what_pushdown_merely_reports()
    {
        const string Sql = "SELECT id FROM members ORDER BY id";

        var permissive = Track(TenancyAdoFixture.CreateDuckDb(capabilities: WithoutInLists()));
        var (report, text) = await PlanAsync(permissive, Sql, TwoOrgManager);
        Assert.False(Assert.Single(report.Entitlements.Tables, t => t.Table == "members").RowPredicatePushed);
        Assert.DoesNotContain("IN (1, 2)", text, StringComparison.Ordinal);

        var required = Track(
            TenancyAdoFixture.CreateDuckDb(Enforcement.PushdownRequired, capabilities: WithoutInLists()));
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            async () => await PlanAsync(required, Sql, TwoOrgManager));
        Assert.Contains("members", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("PUSHDOWN_REQUIRED", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>And it is satisfied silently when the shape does reach the source.</summary>
    [Fact]
    public async Task Q21_pushdown_required_is_satisfied_by_a_source_that_takes_the_shape()
    {
        var required = Track(TenancyAdoFixture.CreateDuckDb(Enforcement.PushdownRequired));
        var (report, _) = await PlanAsync(required, "SELECT id FROM members", TwoOrgManager);

        Assert.True(Assert.Single(report.Entitlements.Tables, t => t.Table == "members").RowPredicatePushed);
    }

    /// <summary>
    /// A mask never reaches the source unless the table declares <c>push_masks</c> and the source
    /// declares <c>supports_mask_pushdown</c> (D153). That is the direction that matters: with
    /// either flag missing the mask is <em>not permitted</em> to travel and never does. With both,
    /// it is permitted and the cost model decides — over six rows it usually still computes it here
    /// — so what this asserts of the permitted case is that the answer does not depend on where.
    /// </summary>
    [Fact]
    public async Task A_mask_never_reaches_the_source_without_both_flags()
    {
        const string Sql = "SELECT id, first_name FROM members ORDER BY id";

        var neither = Duck();
        var tableOnly = Track(TenancyAdoFixture.CreateDuckDb(pushMasks: true));
        var sourceOnly = Track(TenancyAdoFixture.CreateDuckDb(capabilities: TakingMasks()));
        var both = Track(
            TenancyAdoFixture.CreateDuckDb(pushMasks: true, capabilities: TakingMasks()));

        foreach (var fixture in new[] { neither, tableOnly, sourceOnly })
        {
            Assert.DoesNotContain(
                "SUBSTRING",
                (await PlanAsync(fixture, Sql, TenancyFixture.U2)).Remote,
                StringComparison.OrdinalIgnoreCase);
        }

        // Permitted or not, the caller sees the same masked values: the flag is an optimisation and
        // never a disclosure.
        Assert.Equal(
            await RemoteRowsAsync(neither, Sql, TenancyFixture.U2),
            await RemoteRowsAsync(both, Sql, TenancyFixture.U2));
    }

    // ---------------------------------------------------------------- the oracle (§4, §7)

    /// <summary>
    /// The policy conformance run for a remote source: the vectorised engine over the pushed plan
    /// against the reference executor, which fetches the table through the source's own scan path
    /// and evaluates every predicate itself (D39). They must agree row for row, for every principal.
    /// </summary>
    [Theory]
    [InlineData("SELECT id, first_name, last_name FROM members ORDER BY id")]
    [InlineData("SELECT id, note FROM orders ORDER BY id")]
    [InlineData("SELECT id, national_id, postcode FROM members ORDER BY id")]
    public async Task The_two_engines_agree_on_every_principal(string sql)
    {
        var fixture = Duck();
        foreach (var (name, context) in TenancyFixture.Principals)
        {
            Assert.Equal(
                await OrRefusalAsync(() => RemoteRowsAsync(fixture, sql, context)),
                await OrRefusalAsync(() => RemoteRowsAsync(fixture, sql, context, reference: true)));
            Assert.NotNull(name);
        }
    }

    // ---------------------------------------------------------------- the key set (F45)

    /// <summary>
    /// A bound list above the fold ceiling reaches the source as M5's key set (F45,
    /// <c>docs/design/16-entitlements.md</c> §2 item 2 and §3.7 item 2).
    /// </summary>
    /// <remarks>
    /// A 200-organization grant is far above the default <c>fold_max_rows</c> of 64, so nothing is
    /// made literal: the membership becomes a semi-join against the bound list, and the list's keys
    /// travel to the source in the predicate of a lookup call rather than the table's rows travelling
    /// here. What is asserted is the whole of §3.7 for that read — the key set is in the remote text,
    /// the report says the row predicate was pushed, the rows the source was asked for never exceed
    /// the principal's scope, and the answer is the one the folded plan gives.
    /// </remarks>
    [Fact]
    public async Task Q159_a_list_above_the_fold_ceiling_reaches_duckdb_as_a_key_set() =>
        await AssertKeySetAsync(Duck());

    /// <summary>The same over a second real dialect, for the same reason as every other pair here.</summary>
    [Fact]
    public async Task Q159_a_list_above_the_fold_ceiling_reaches_postgresql_as_a_key_set()
    {
        var server = postgres.Fixture;
        if (server.SkipReason is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        await AssertKeySetAsync(Track(TenancyAdoFixture.CreatePostgres(
            server.CreateDatabase("chalk_keyset"), s => new Npgsql.NpgsqlConnection(s))));
    }

    private async Task AssertKeySetAsync(TenancyAdoFixture fixture)
    {
        const string Sql = "SELECT id, org_id FROM members ORDER BY id";

        // 200 organisations, of which the fixture holds three. Above the ceiling, so the list stays
        // a relation and its rows are not in the plan.
        var many = TenancyFixture.Principal(
            user: 1,
            managerOrgs: [.. Enumerable.Range(1, 200)],
            agentOrgs: [],
            auditorOrgs: [],
            subjectPairs: []);
        Assert.True(many.Lists["manager_orgs"].Rows.Count > RequestContext.DefaultFoldMaxRows);

        var (report, remote) = await PlanAsync(fixture, Sql, many);

        // The membership is in the predicate the source evaluates, as one parameterised key set —
        // the executor binds a call's worth of keys into that single placeholder.
        Assert.Contains("\"org_id\" IN (?)", remote, StringComparison.Ordinal);
        // And none of the identifiers is in the plan: the ceiling is what keeps them out.
        Assert.DoesNotContain("IN (1, 2", remote, StringComparison.Ordinal);
        Assert.True(Assert.Single(report.Entitlements.Tables, t => t.Table == "members").RowPredicatePushed);

        // The rows the source was asked for are the scope's, not the table's. `members` holds seven
        // rows over three organisations and every one of them is in scope here, so the count that
        // matters is the one below, where the scope is narrower than the table.
        await using var engine = await RemoteEngineAsync(fixture);
        var all = await engine.WithEntitlements().PrepareAsync(Sql, many);
        await using (var execution = await engine.ExecuteAsync(all.Query))
        {
            await DrainAsync(execution);
            Assert.Equal(7, execution.Stats.RowsScanned);
        }

        // One organization's worth of grants, still bound as a list above the ceiling: the source
        // returns that organization's rows and no others, which is what a key set buys over a full
        // fetch and what §3.7 is about.
        var oneOrg = TenancyFixture.Principal(
            user: 1,
            managerOrgs: [1, .. Enumerable.Range(100, 199)],
            agentOrgs: [],
            auditorOrgs: [],
            subjectPairs: []);
        var narrow = await engine.WithEntitlements().PrepareAsync(Sql, oneOrg);
        string[] keySetRows;
        await using (var execution = await engine.ExecuteAsync(narrow.Query))
        {
            keySetRows = await DrainAsync(execution);
            Assert.Equal(["1|1", "2|1"], keySetRows);
            Assert.Equal(2, execution.Stats.RowsScanned);
        }

        // And the same principal's grants folded into a literal IN list give the same answer, which
        // is the only comparison worth making: a plan that ships keys and a plan that ships a list
        // are two spellings of one policy.
        Assert.Equal(
            keySetRows,
            await RemoteRowsAsync(
                fixture,
                Sql,
                TenancyFixture.Principal(
                    user: 1, managerOrgs: [1], agentOrgs: [], auditorOrgs: [], subjectPairs: [])));
    }

    // ------------------------------------------------- a composite key set (F50, ADR 0027)

    /// <summary>
    /// A bound list of <b>pairs</b> above the fold ceiling reaches the source as a key set over
    /// both columns (F50, <c>docs/design/16-entitlements.md</c> §2 item 2 and §3.7 item 2).
    /// </summary>
    /// <remarks>
    /// The tenancy package writes a grant confined to one organization as
    /// <c>(id, org_id) IN (@ctx.subject_pairs)</c>, which above the ceiling decorrelates into a
    /// <em>two-key</em> semi-join. F45's rule matched one key only, so the entitled table was
    /// fetched whole and <c>row_predicate_pushed</c> was honestly false. The pairs bound here are
    /// what tells a key set over the tuple from two key sets over the columns: <c>(3, 1)</c> is in
    /// the list and member 3 is in organization 2, so a cross product of the columns would return
    /// that member and the pair does not.
    /// </remarks>
    [Fact]
    public async Task Q160_a_composite_list_above_the_fold_ceiling_reaches_duckdb_as_a_key_set() =>
        await AssertCompositeKeySetAsync(Duck());

    /// <summary>The same over a second real dialect, which accepts the same spelling.</summary>
    [Fact]
    public async Task Q160_a_composite_list_above_the_fold_ceiling_reaches_postgresql_as_a_key_set()
    {
        var server = postgres.Fixture;
        if (server.SkipReason is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        await AssertCompositeKeySetAsync(Track(TenancyAdoFixture.CreatePostgres(
            server.CreateDatabase("chalk_pairset"), s => new Npgsql.NpgsqlConnection(s))));
    }

    private async Task AssertCompositeKeySetAsync(TenancyAdoFixture fixture)
    {
        const string Sql = "SELECT id, org_id FROM members ORDER BY id";

        // Three pairs that say something about this fixture and 197 that say nothing: 200 in all,
        // far above the default fold_max_rows of 64, so the list stays a relation and its values
        // are not in the plan. `members` holds (1,1) (2,1) (3,2) (4,2) (5,3) (6,3) (7,2), so
        // (1,1) and (4,2) match a row and (3,1) matches none — member 3 is in organization 2.
        int[][] granted = [[1, 1], [3, 1], [4, 2]];
        var pairs = granted
            .Concat(Enumerable.Range(100, 197).Select(i => new[] { i, 9 }))
            .ToArray();
        Assert.Equal(200, pairs.Length);

        var many = TenancyFixture.Principal(
            user: 1, managerOrgs: [], agentOrgs: [], auditorOrgs: [], subjectPairs: pairs);
        Assert.True(many.Lists["subject_pairs"].Rows.Count > RequestContext.DefaultFoldMaxRows);

        var (report, remote) = await PlanAsync(fixture, Sql, many);

        // Both key columns in one key set, one placeholder for the whole list of key rows, which
        // the executor expands into `(?, ?), (?, ?), …` per call.
        Assert.Contains("(\"id\", \"org_id\") IN (?)", remote, StringComparison.Ordinal);
        // And none of the pairs is in the plan: the ceiling is what keeps them out.
        Assert.DoesNotContain("IN (1, 3", remote, StringComparison.Ordinal);
        Assert.True(Assert.Single(report.Entitlements.Tables, t => t.Table == "members").RowPredicatePushed);

        // The rows the source was asked for are the scope's: two of the seven, and not the three a
        // key set over each column separately would have matched.
        await using var engine = await RemoteEngineAsync(fixture);
        var prepared = await engine.WithEntitlements().PrepareAsync(Sql, many);
        string[] keySetRows;
        await using (var execution = await engine.ExecuteAsync(prepared.Query))
        {
            keySetRows = await DrainAsync(execution);
            Assert.Equal(["1|1", "4|2"], keySetRows);
            Assert.Equal(2, execution.Stats.RowsScanned);
        }

        // And the same grants folded into a literal list give the same answer, which is the only
        // comparison worth making: a plan that ships key rows and a plan that ships a list are two
        // spellings of one policy.
        Assert.Equal(
            keySetRows,
            await RemoteRowsAsync(
                fixture,
                Sql,
                TenancyFixture.Principal(
                    user: 1,
                    managerOrgs: [],
                    agentOrgs: [],
                    auditorOrgs: [],
                    subjectPairs: granted)));
    }

    // ---------------------------------------------------------------- PostgreSQL

    /// <summary>
    /// The same over a second real dialect. Skipped without a server, and compulsory under
    /// <c>CHALK_TEST_POSTGRES_REQUIRED</c>.
    /// </summary>
    [Fact]
    public async Task The_same_answers_and_the_same_pushdown_over_postgresql()
    {
        var server = postgres.Fixture;
        if (server.SkipReason is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        var fixture = Track(TenancyAdoFixture.CreatePostgres(
            server.CreateDatabase("chalk_tenancy"), s => new Npgsql.NpgsqlConnection(s)));

        const string Sql = "SELECT id, first_name FROM members ORDER BY id";
        foreach (var (_, context) in TenancyFixture.Principals)
        {
            Assert.Equal(
                await PocoRowsAsync(Sql, context),
                await RemoteRowsAsync(fixture, Sql, context));
        }

        var (report, text) = await PlanAsync(fixture, Sql, TwoOrgManager);
        Assert.True(Assert.Single(report.Entitlements.Tables, t => t.Table == "members").RowPredicatePushed);
        Assert.Contains("IN (1, 2)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SUBSTRING", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- through a parent (§3.13)

    /// <summary>
    /// D229's first half: the parent and the child in one source, so the join, the parent's folded
    /// predicate and the key condition go to the source as <b>one remote query</b> — and the rows it
    /// is asked for are the visible ones.
    /// </summary>
    /// <remarks>
    /// Read against the two-hundred-row-fetch it replaces: <c>messages</c> holds five rows over four
    /// threads, three of which this principal can see, and the source returns four rows rather than
    /// five. The parent's own predicate is in the text, which is what
    /// <c>row_predicate_pushed</c> claims for both tables — the child has no predicate of its own,
    /// so what it means for it is that the join carrying the parent's went to the source.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_parent_in_the_same_source_pushes_as_one_remote_query(bool postgres_)
    {
        var fixture = await ThroughFixtureAsync(postgres_, ThroughPlacement.OneSource, name: "one");
        if (fixture is null)
        {
            return;
        }

        const string Sql = "SELECT id FROM messages ORDER BY id";
        var (report, text) = await PlanAsync(fixture, Sql, TenancyFixture.U1);

        Assert.Contains("\"threads\"", text, StringComparison.Ordinal);
        Assert.Contains("\"org_id\" IN (1, 2)", text, StringComparison.Ordinal);
        Assert.Contains("\"thread_id\"", text, StringComparison.Ordinal);
        Assert.True(
            Assert.Single(report.Entitlements.Tables, t => t.Table == "threads").RowPredicatePushed);
        Assert.True(
            Assert.Single(report.Entitlements.Tables, t => t.Table == "messages").RowPredicatePushed);

        await using var engine = await RemoteEngineAsync(fixture);
        var prepared = await engine.WithEntitlements().PrepareAsync(Sql, TenancyFixture.U1);
        await using (var execution = await engine.ExecuteAsync(prepared.Query))
        {
            Assert.Equal(["1", "2", "3", "4"], await DrainAsync(execution));
            // Four of the five rows, and none of the thread this principal cannot see.
            Assert.Equal(4, execution.Stats.RowsScanned);
        }

        // And the same statement in process gives the same answer, which is the comparison that
        // makes the remote one worth anything.
        Assert.Equal(await PocoRowsAsync(Sql, TenancyFixture.U1), await RemoteRowsAsync(fixture, Sql, TenancyFixture.U1));
    }

    /// <summary>
    /// D229's second half (F52): the parent in one source and the child in another, so the join
    /// cannot go to either — and the <b>parent</b> drives M5's lookup instead, its visible keys
    /// reaching the child's source as a key set.
    /// </summary>
    /// <remarks>
    /// Read against the whole fetch it replaces: <c>messages</c> holds five rows over four threads
    /// and the source is asked for the four whose thread this principal can see, in one call of at
    /// most <c>max_in_list</c> keys. <c>row_predicate_pushed</c> is true for the child because the
    /// key set is in the remote text, which is the same thing it means for the single-source plan:
    /// the rows the source is asked for are the visible ones.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_parent_in_another_source_ships_its_visible_keys_as_a_key_set(bool postgres_)
    {
        var fixture = await ThroughFixtureAsync(
            postgres_, ThroughPlacement.ParentInProcess, name: "keyset");
        if (fixture is null)
        {
            return;
        }

        const string Sql = "SELECT id FROM messages ORDER BY id";
        var (report, text) = await PlanAsync(fixture, Sql, TenancyFixture.U1);

        // One remote query, for the child alone — the parent is not in this source to be joined to —
        // with the parent's visible keys bound into its one placeholder.
        Assert.DoesNotContain("\"threads\"", text, StringComparison.Ordinal);
        Assert.Contains("\"thread_id\" IN (?)", text, StringComparison.Ordinal);
        Assert.True(
            Assert.Single(report.Entitlements.Tables, t => t.Table == "messages").RowPredicatePushed);
        Assert.Equal(
            TableVisibility.Some,
            Assert.Single(report.Entitlements.Tables, t => t.Table == "messages").Visibility);

        var ok = false;
        await using (var engine = await RemoteEngineAsync(fixture))
        {
            var prepared = await engine.WithEntitlements().PrepareAsync(Sql, TenancyFixture.U1);
            await using var execution = await engine.ExecuteAsync(prepared.Query);
            var result = await DrainAsync(execution);
            Assert.Equal(["1", "2", "3", "4"], result);
            // Four of the five rows come back from the child's source, in one call: the scope, not
            // the table. `RowsScanned` counts the parent's four rows here beside them, because the
            // parent is read in process rather than inside the child's query.
            Assert.Equal(4, execution.Stats.RowsFetched);
            Assert.Equal(1, execution.Stats.RemoteCalls);
            Assert.Equal(8, execution.Stats.RowsScanned);
            ok = true;
        }
        
        Assert.True(ok);

        // The answer is the same as the same-source plan's, which is the comparison that makes the
        // exchange worth anything.
        var sameSource = await ThroughFixtureAsync(
            postgres_, ThroughPlacement.OneSource, name: "keysetref");
        Assert.NotNull(sameSource);
        Assert.Equal(
            await RemoteRowsAsync(sameSource, Sql, TenancyFixture.U1),
            await RemoteRowsAsync(fixture, Sql, TenancyFixture.U1));
        Assert.Equal(await PocoRowsAsync(Sql, TenancyFixture.U1), await RemoteRowsAsync(fixture, Sql, TenancyFixture.U1));
    }

    /// <summary>
    /// F54: the child's own projection folded to a <b>constant</b> — an unconditional mask, or the
    /// stand-in a principal with no rule for the column gets — and the key set still reaches the
    /// source. The projection stays above the boundary, which is what §3.8 requires of it.
    /// </summary>
    /// <remarks>
    /// This is V172's shape, and until F54 it was the one member of this family that fetched the
    /// child whole: a constant is not the permutation a key set could be pushed under, so the key
    /// set went nowhere and <c>row_predicate_pushed</c> was honestly false. The key set now goes to
    /// the <em>scan</em> beneath the projection; the source is asked for the visible rows and for
    /// plain columns, and the mask is computed here over what comes back.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_constant_projection_no_longer_keeps_the_cross_source_child_whole(bool postgres_)
    {
        var fixture = await ThroughFixtureAsync(
            postgres_, ThroughPlacement.ParentInProcess, name: "f54");
        if (fixture is null)
        {
            return;
        }

        const string Sql = "SELECT id, content FROM messages ORDER BY id";

        // u2 is an agent in organization 1, for whom the mask is unconditional over every thread
        // they can see; u6 is an auditor there, for whom no rule names `content` at all and the
        // stand-in is a constant. Both fold the child's projection to something that is not a
        // permutation of its own columns — and the key set reaches the source either way, byte for
        // byte the same on both dialects. u6's stand-in reads no column, so `content` is not even
        // fetched.
        (RequestContext Context, string Remote)[] principals =
        [
            (TenancyFixture.U2,
                "SELECT \"id\", \"thread_id\", \"content\" FROM \"messages\" WHERE \"thread_id\" IN (?)"),
            (TenancyFixture.U6,
                "SELECT \"id\", \"thread_id\" FROM \"messages\" WHERE \"thread_id\" IN (?)"),
        ];

        foreach (var (context, remote) in principals)
        {
            var (report, text) = await PlanAsync(fixture, Sql, context);
            Assert.Equal(remote, text);
            Assert.DoesNotContain("SUBSTRING", text, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                Assert.Single(report.Entitlements.Tables, t => t.Table == "messages")
                    .RowPredicatePushed);

            await using (var engine = await RemoteEngineAsync(fixture))
            {
                var prepared = await engine.WithEntitlements().PrepareAsync(Sql, context);
                await using var execution = await engine.ExecuteAsync(prepared.Query);
                await DrainAsync(execution);
                // Three of the five rows: organization 1's two threads, not the table.
                Assert.Equal(3, execution.Stats.RowsFetched);
                Assert.Equal(1, execution.Stats.RemoteCalls);
            }

            // And what comes back is what the same statement in process discloses.
            Assert.Equal(
                await PocoRowsAsync(Sql, context), await RemoteRowsAsync(fixture, Sql, context));
        }
    }

    /// <summary>
    /// The reverse placement: the <b>child</b> in process and the parent in the database. There is
    /// no source to ship a key set to, so what travels is the parent's own entitled scan — its rows
    /// are the visible threads and nothing else — and the join runs here over them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_child_in_process_joins_to_its_parents_entitled_scan(bool postgres_)
    {
        var fixture = await ThroughFixtureAsync(
            postgres_, ThroughPlacement.ChildInProcess, name: "reverse");
        if (fixture is null)
        {
            return;
        }

        const string Sql = "SELECT id FROM messages ORDER BY id";
        var (report, text) = await PlanAsync(fixture, Sql, TenancyFixture.U1);

        // The one remote query is the parent's, with its folded predicate and nothing of the child.
        Assert.Contains("\"threads\"", text, StringComparison.Ordinal);
        Assert.Contains("\"org_id\" IN (1, 2)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IN (?)", text, StringComparison.Ordinal);
        Assert.True(
            Assert.Single(report.Entitlements.Tables, t => t.Table == "threads").RowPredicatePushed);
        // The child has no source to push into, so the flag is honestly false for it.
        Assert.False(
            Assert.Single(report.Entitlements.Tables, t => t.Table == "messages").RowPredicatePushed);

        var sameSource = await ThroughFixtureAsync(
            postgres_, ThroughPlacement.OneSource, name: "reverseref");
        Assert.NotNull(sameSource);
        Assert.Equal(
            await RemoteRowsAsync(sameSource, Sql, TenancyFixture.U1),
            await RemoteRowsAsync(fixture, Sql, TenancyFixture.U1));
    }

    /// <summary>
    /// <c>PUSHDOWN_REQUIRED</c> on a cross-source child is satisfied by the exchange, and refused —
    /// naming the shape — where the child's source takes no <c>IN</c> list for the keys to travel in.
    /// </summary>
    [Fact]
    public async Task Pushdown_required_on_a_cross_source_child_is_satisfied_by_the_key_set()
    {
        var satisfied = await ThroughFixtureAsync(
            postgres_: false, ThroughPlacement.ParentInProcess, Enforcement.PushdownRequired);
        Assert.NotNull(satisfied);
        var (report, _) = await PlanAsync(satisfied, "SELECT id FROM messages", TenancyFixture.U1);
        Assert.True(
            Assert.Single(report.Entitlements.Tables, t => t.Table == "messages").RowPredicatePushed);

        var refused = await ThroughFixtureAsync(
            postgres_: false,
            ThroughPlacement.ParentInProcess,
            Enforcement.PushdownRequired,
            WithoutInLists());
        Assert.NotNull(refused);
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            async () => await PlanAsync(refused, "SELECT id FROM messages", TenancyFixture.U1));

        Assert.Contains("PUSHDOWN_REQUIRED", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("derives through", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("PREDICATE_SHAPE_IN", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the child's own projection must stay in process — a mask does that, by §3.8 — the join
    /// stays with it, and when the two tables are in <em>one</em> remote source it is a local join
    /// over two fetches. It is a <b>hash</b> join, which is what F51's cost correction made it.
    /// </summary>
    /// <remarks>
    /// Until F51 a nested-loop join whose inner side is a remote query was charged as though the
    /// fetch were free, so it won against the hash join that would have been valid, and
    /// <c>CrossSourceSupport</c> refused the whole plan as UNSUPPORTED — the backstop working for a
    /// defect upstream of it. This is the same statement, planned and run. ADR 0025 part 2h.
    /// </remarks>
    [Fact]
    public async Task A_masked_child_beside_its_parent_in_one_source_joins_by_hash()
    {
        var fixture = await ThroughFixtureAsync(
            postgres_: false, ThroughPlacement.OneSource, name: "hash");
        Assert.NotNull(fixture);

        const string Sql = "SELECT id, content FROM messages ORDER BY id";
        await using var engine = await RemoteEngineAsync(fixture);
        var prepared = await engine.WithEntitlements().PrepareAsync(Sql, TenancyFixture.U1);

        Assert.Contains(Rel.KindOneofCase.HashJoin, KindsOf(prepared.Plan));
        Assert.DoesNotContain(Rel.KindOneofCase.NestedLoopJoin, KindsOf(prepared.Plan));

        Assert.Equal(
            await PocoRowsAsync(Sql, TenancyFixture.U1),
            await RemoteRowsAsync(fixture, Sql, TenancyFixture.U1));
    }

    // ------------------------------------ a context with an open half (F55, §2.1, §3.13)

    /// <summary>
    /// Chapter 12's statement over a real source: the entitled child, its <c>through</c> parent and
    /// a third table of that same source, planned three times — nothing bound, the tenancy folded,
    /// everything folded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With an open half the parent's entitled leaf becomes a <em>local</em> subtree — the
    /// memberships are joins to context tables, which belong to no source — so the join a
    /// <c>Through</c> compiles into has a local driving side and a remote child, and the statement's
    /// own join to <c>members</c> has that whole thing on one side. Until F55 the parent's keys were
    /// not offered to the child's source at all, and the plan a nested loop then won was refused by
    /// I-IR-20. Now the keys travel as a key set, and the joins above are hash joins.
    /// </para>
    /// <para>
    /// The three plans are read by what leaves the process: under the shape the child's source is
    /// asked for the keys the parent computed here; with the tenancy folded the parent's own
    /// predicate is in its own SQL, which is what a fold is for; with everything folded the pair is
    /// one remote query again. The rows are the same three times and the same as the in-process
    /// fixture's, the digests are three, and each plan is the plan preparing with the union would
    /// have given.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_open_half_plans_over_a_remote_catalog_and_narrows_to_two_more(bool postgres_)
    {
        var fixture = await ThroughFixtureAsync(postgres_, ThroughPlacement.OneSource, name: "open");
        if (fixture is null)
        {
            return;
        }

        const string Sql =
            "SELECT m.id, t.org_id, p.first_name, m.content FROM messages m"
            + " JOIN threads t ON t.id = m.thread_id"
            + " JOIN members p ON p.id = t.member_id"
            + " ORDER BY m.id";

        // An auditor in two organisations: the threads are visible, and `first_name` comes back as
        // `FINGERPRINT(first_name, @ctx.mask_key)` — which is the one name this statement's
        // descriptors read that is the person's rather than the organization's, and so the one
        // thing the tenant's shared plan cannot fold.
        var agent = TenancyFixture.Principal(
            user: 2, managerOrgs: [], agentOrgs: [], auditorOrgs: [1, 2], subjectPairs: []);

        await using var engine = await RemoteEngineAsync(fixture);
        var entitled = engine.WithEntitlements();

        var basePlan = await entitled.PrepareAsync(Sql, agent.Shape());
        var tenantPlan = await basePlan.NarrowAsync(Only(agent, person: false));
        var memberPlan = await tenantPlan.NarrowAsync(Only(agent, person: true));

        // The shape: the parent's visible keys are computed here and bound into the child's query.
        Assert.Contains(
            RemoteTextsOf(basePlan.Plan),
            text => text.Contains("\"thread_id\" IN (?)", StringComparison.Ordinal));
        Assert.Equal(["mask_key"], tenantPlan.Query.RequiredContext.Order().ToArray());

        // The tenancy folded: the parent's own predicate is in the parent's own SQL.
        Assert.Contains(
            RemoteTextsOf(tenantPlan.Plan),
            text => text.Contains("\"threads\"", StringComparison.Ordinal)
                && text.Contains("\"org_id\" IN (1, 2)", StringComparison.Ordinal));
        Assert.Empty(memberPlan.Query.RequiredContext);

        // Three plans, three digests: what is folded is in the plan, so it is in the digest.
        Assert.Equal(
            3,
            new HashSet<ulong>
            {
                basePlan.PlanDigest, tenantPlan.PlanDigest, memberPlan.PlanDigest,
            }.Count);

        // And the same rows three times, equal to the in-process fixture's.
        var expected = await PocoRowsAsync(Sql, agent);
        Assert.Equal(expected, await RowsOfAsync(engine, basePlan.Query, agent));
        // The whole context at execution: the open half is bound and the folded half is checked
        // against what the plan already answers for (V179).
        Assert.Equal(expected, await RowsOfAsync(engine, tenantPlan.Query, agent));
        Assert.Equal(expected, await RowsOfAsync(engine, memberPlan.Query, context: null));

        // D233's identity, over a remote catalog: a narrowing is preparing with the union.
        await AssertNarrowingIsPreparingAsync(
            entitled, Sql, tenantPlan, agent.Shape(TenancyFixture.PersonNames));
        await AssertNarrowingIsPreparingAsync(entitled, Sql, memberPlan, agent);
    }

    /// <summary>
    /// And where the source takes no <c>IN</c> list there is no key set to ship: the child is
    /// fetched whole and the joins above it are hash joins, which is the plan F51's charge reaching
    /// this shape chooses. <c>PUSHDOWN_REQUIRED</c> is what turns that into a refusal.
    /// </summary>
    [Fact]
    public async Task An_open_half_over_a_source_that_takes_no_in_list_still_plans()
    {
        var fixture = await ThroughFixtureAsync(
            postgres_: false, ThroughPlacement.OneSource, capabilities: WithoutInLists(), name: "openno");
        Assert.NotNull(fixture);

        const string Sql =
            "SELECT m.id, t.org_id, m.content FROM messages m"
            + " JOIN threads t ON t.id = m.thread_id ORDER BY m.id";
        var agent = TenancyFixture.Principal(
            user: 2, managerOrgs: [], agentOrgs: [1, 2], auditorOrgs: [], subjectPairs: []);

        await using var engine = await RemoteEngineAsync(fixture);
        var prepared = await engine.WithEntitlements().PrepareAsync(Sql, agent.Shape());

        Assert.DoesNotContain(
            RemoteTextsOf(prepared.Plan), t => t.Contains("IN (?)", StringComparison.Ordinal));
        Assert.Contains(Rel.KindOneofCase.HashJoin, KindsOf(prepared.Plan));
        Assert.False(
            Assert.Single(prepared.Entitlements.Tables, t => t.Table == "messages")
                .RowPredicatePushed);
        Assert.Equal(
            await PocoRowsAsync(Sql, agent), await RowsOfAsync(engine, prepared.Query, agent));
    }

    /// <summary>And named as a refusal where the host declared <c>PUSHDOWN_REQUIRED</c>.</summary>
    [Fact]
    public async Task Pushdown_required_under_an_open_half_names_the_shape_it_could_not_use()
    {
        var fixture = await ThroughFixtureAsync(
            postgres_: false,
            ThroughPlacement.OneSource,
            Enforcement.PushdownRequired,
            WithoutInLists(),
            name: "openreq");
        Assert.NotNull(fixture);

        await using var engine = await RemoteEngineAsync(fixture);
        var agent = TenancyFixture.Principal(
            user: 2, managerOrgs: [], agentOrgs: [1, 2], auditorOrgs: [], subjectPairs: []);

        var refused = await Assert.ThrowsAsync<EntitlementException>(
            async () => await engine.WithEntitlements()
                .PrepareAsync("SELECT id FROM messages", agent.Shape()));

        Assert.Contains("PUSHDOWN_REQUIRED", refused.Message, StringComparison.Ordinal);
        Assert.Contains("messages", refused.Message, StringComparison.Ordinal);
        Assert.Contains("threads", refused.Message, StringComparison.Ordinal);
        Assert.Contains("PREDICATE_SHAPE_IN", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The half of a principal's bindings that is the <em>person's</em> — what tells one principal
    /// of a tenant from another — or everything else, which is what a tenant's plan may fold.
    /// </summary>
    private static RequestContext Only(RequestContext whole, bool person) => new()
    {
        Scalars = whole.Scalars
            .Where(s => TenancyFixture.PersonNames.Contains(s.Key, StringComparer.Ordinal) == person)
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal),
        Lists = whole.Lists
            .Where(l => TenancyFixture.PersonNames.Contains(l.Key, StringComparer.Ordinal) == person)
            .ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal),
    };

    /// <summary>D233's identity: a narrowed plan is the plan preparing with the union gives.</summary>
    private static async Task AssertNarrowingIsPreparingAsync(
        EntitledEngine entitled, string sql, EntitledQuery narrowed, RequestContext union)
    {
        var prepared = await entitled.PrepareAsync(sql, union);
        Assert.Equal(prepared.PlanDigest, narrowed.PlanDigest);
        Assert.Equal(
            Google.Protobuf.MessageExtensions.ToByteArray(prepared.Plan),
            Google.Protobuf.MessageExtensions.ToByteArray(narrowed.Plan));
    }

    private static async Task<string[]> RowsOfAsync(
        ChalkEngine engine, PreparedQuery prepared, RequestContext? context)
    {
        await using var execution = context is null
            ? await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null)
            : await engine.ExecuteAsync(prepared, context);
        return await DrainAsync(execution);
    }

    /// <summary>The generated SQL of every remote query in the plan.</summary>
    private static IReadOnlyList<string> RemoteTextsOf(Plan plan)
    {
        var found = new List<string>();
        Collect(plan.Root!, found);
        return found;

        static void Collect(Rel rel, List<string> into)
        {
            if (rel.KindCase == Rel.KindOneofCase.RemoteQuery)
            {
                into.Add(rel.RemoteQuery.QueryText);
                return;
            }

            foreach (var input in PlanWalker.Inputs(rel))
            {
                Collect(input, into);
            }
        }
    }

    /// <summary>Every rel kind in a plan, for an assertion about which join the planner chose.</summary>
    private static IReadOnlyList<Rel.KindOneofCase> KindsOf(Plan plan)
    {
        var kinds = new List<Rel.KindOneofCase>();
        Collect(plan.Root!, kinds);
        return kinds;

        static void Collect(Rel rel, List<Rel.KindOneofCase> into)
        {
            into.Add(rel.KindCase);
            foreach (var input in PlanWalker.Inputs(rel))
            {
                Collect(input, into);
            }
        }
    }

    /// <summary>
    /// The fixture for one dialect, or null where PostgreSQL has no server — which
    /// <c>CHALK_TEST_POSTGRES_REQUIRED</c> turns into a failure rather than a skip.
    /// </summary>
    private async Task<TenancyAdoFixture?> ThroughFixtureAsync(
        bool postgres_,
        ThroughPlacement placement,
        Enforcement enforcement = Enforcement.Pushdown,
        SourceCapabilities? capabilities = null,
        string name = "")
    {
        await Task.CompletedTask;
        if (!postgres_)
        {
            return Track(TenancyAdoFixture.CreateDuckDb(
                enforcement, capabilities: capabilities, placement: placement));
        }

        var server = postgres.Fixture;
        if (server.SkipReason is { } reason)
        {
            Assert.Skip(reason);
            return null;
        }

        return Track(TenancyAdoFixture.CreatePostgres(
            // One database per fixture: `createdb` refuses a name that is already there, and two
            // tests wanting the same placement is not two tests sharing one database.
            server.CreateDatabase(("chalk_thr_" + name + "_" + placement).ToLowerInvariant()),
            s => new Npgsql.NpgsqlConnection(s),
            enforcement,
            capabilities: capabilities,
            placement: placement));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A manager in two organisations, so the folded predicate is a two-element IN list.</summary>
    private static readonly RequestContext TwoOrgManager = TenancyFixture.Principal(
        user: 1, managerOrgs: [1, 2], agentOrgs: [], auditorOrgs: [], subjectPairs: []);

    private static SourceCapabilities WithoutInLists()
    {
        var full = AdoCapabilities.For(DialectProfiles.DuckDb);
        return new SourceCapabilities
        {
            QueryLanguage = full.QueryLanguage,
            PushablePredicates =
                [.. full.PushablePredicates.Where(p => p != PredicateShape.In)],
            PushableFunctions = full.PushableFunctions,
            PushableAggregates = full.PushableAggregates,
            NativeFunctions = full.NativeFunctions,
            UnsupportedFunctions = full.UnsupportedFunctions,
            SupportsProject = full.SupportsProject,
            SupportsSort = full.SupportsSort,
            SupportsLimit = full.SupportsLimit,
            SupportsOffset = full.SupportsOffset,
            SupportsDistinct = full.SupportsDistinct,
            SupportsGroupBy = full.SupportsGroupBy,
            SupportsHaving = full.SupportsHaving,
            SupportsInnerJoin = full.SupportsInnerJoin,
            SupportsOuterJoin = full.SupportsOuterJoin,
            SupportsSemiAntiJoin = full.SupportsSemiAntiJoin,
            MaxPushdownRows = full.MaxPushdownRows,
            MaxInList = 0,
            SupportsParameters = full.SupportsParameters,
        };
    }

    private static SourceCapabilities TakingMasks()
    {
        var full = AdoCapabilities.For(DialectProfiles.DuckDb);
        return new SourceCapabilities
        {
            QueryLanguage = full.QueryLanguage,
            PushablePredicates = full.PushablePredicates,
            PushableFunctions = [.. full.PushableFunctions, FunctionId.Substring],
            PushableAggregates = full.PushableAggregates,
            NativeFunctions = full.NativeFunctions,
            UnsupportedFunctions = full.UnsupportedFunctions,
            SupportsProject = full.SupportsProject,
            SupportsSort = full.SupportsSort,
            SupportsLimit = full.SupportsLimit,
            SupportsOffset = full.SupportsOffset,
            SupportsDistinct = full.SupportsDistinct,
            SupportsGroupBy = full.SupportsGroupBy,
            SupportsHaving = full.SupportsHaving,
            SupportsInnerJoin = full.SupportsInnerJoin,
            SupportsOuterJoin = full.SupportsOuterJoin,
            SupportsSemiAntiJoin = full.SupportsSemiAntiJoin,
            MaxPushdownRows = full.MaxPushdownRows,
            MaxInList = full.MaxInList,
            SupportsParameters = full.SupportsParameters,
            SupportsMaskPushdown = true,
        };
    }

    private TenancyAdoFixture Duck() => Track(TenancyAdoFixture.Shared, own: false);

    private TenancyAdoFixture Track(TenancyAdoFixture fixture, bool own = true)
    {
        if (own)
        {
            _fixtures.Add(fixture);
        }

        return fixture;
    }

    private async Task<(EntitledQuery Report, string Remote)> PlanAsync(
        TenancyAdoFixture fixture, string sql, RequestContext context)
    {
        await using var engine = await RemoteEngineAsync(fixture);
        var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
        return (prepared, RemoteTextOf(prepared.Plan));
    }

    /// <summary>The generated SQL of the one remote query in the plan.</summary>
    private static string RemoteTextOf(Plan plan)
    {
        var found = new List<string>();
        Collect(plan.Root!, found);
        return Assert.Single(found);

        static void Collect(Rel rel, List<string> into)
        {
            if (rel.KindCase == Rel.KindOneofCase.RemoteQuery)
            {
                into.Add(rel.RemoteQuery.QueryText);
                return;
            }

            foreach (var input in PlanWalker.Inputs(rel))
            {
                Collect(input, into);
            }
        }
    }

    private async Task<ChalkEngine> RemoteEngineAsync(
        TenancyAdoFixture fixture, bool reference = false) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
            Execution = reference
                ? new ExecutionOptions { Engine = ExecutionEngine.Reference }
                : new ExecutionOptions(),
        });

    private async Task<string[]> RemoteRowsAsync(
        TenancyAdoFixture fixture, string sql, RequestContext context, bool reference = false)
    {
        await using var engine = await RemoteEngineAsync(fixture, reference);
        // The oracle runs at PUSHDOWN_LEVEL_NONE (D39): a remote table is a plain scan through the
        // source's own path and every predicate is evaluated here, so the two engines share nothing
        // but the rows.
        return await RowsAsync(
            engine,
            sql,
            context,
            reference ? new PrepareOptions { Pushdown = PushdownLevel.None } : null);
    }

    /// <summary>
    /// The rows, or the refusal as one row: two paths that refuse the same statement for the same
    /// principal have said the same thing, and comparing the refusal is what says so.
    /// </summary>
    private static async Task<string[]> OrRefusalAsync(Func<Task<string[]>> rows)
    {
        try
        {
            return await rows();
        }
        catch (EntitlementException refused)
        {
            var stop = refused.Message.IndexOf(". ", StringComparison.Ordinal);
            var sentence = stop < 0 ? refused.Message : refused.Message[..stop];
            // The schema is where the table lives and not what the refusal is about: `main` here,
            // the source's own name there, one refusal.
            return
            [
                "POLICY "
                + sentence
                    .Replace(TenancyAdoFixture.SourceId + ".", "<schema>.", StringComparison.Ordinal)
                    .Replace("main.", "<schema>.", StringComparison.Ordinal),
            ];
        }
    }

    private async Task<string[]> PocoRowsAsync(string sql, RequestContext context)
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Shared.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });
        return await RowsAsync(engine, sql, context);
    }

    /// <summary>Every row of an execution, so its statistics can be read once it has finished.</summary>
    private static async Task<string[]> DrainAsync(QueryExecution execution)
    {
        var rows = new List<string>();
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

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine, string sql, RequestContext context, PrepareOptions? options = null)
    {
        var prepared = options is null
            ? await engine.WithEntitlements().PrepareAsync(sql, context)
            : await engine.WithEntitlements().PrepareAsync(sql, context, options);
        var rows = new List<string>();
        await using var execution =
            await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
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

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
        {
            fixture.Dispose();
        }
    }
}
