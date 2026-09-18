using System.Globalization;
using System.Text;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Namespace confusion: one policy, two sources holding a table called <c>orders</c> and a table
/// called <c>order_items</c> each, and every leaf held to <b>its own</b> source's rules
/// (<c>docs/design/45-typed-tenancy-surface.md</c> §1–§2 as amended 2026-09-16, D270).
/// </summary>
/// <remarks>
/// <para>
/// This is the evidence for the amendment that a <see cref="Table"/> handle is obtained only through
/// a <see cref="Source"/>. The claim is narrow and the failure it guards against is not: a lookup by
/// bare table name would hand the ledger's descriptor to the archive's read, and the result would be
/// a plan that looks entirely ordinary and discloses the wrong source's rows. So the assertions here
/// are about identity rather than about shape — which descriptor each leaf was compiled under, which
/// schema each reported table and each explained path step names, and which rows and which values
/// each principal actually receives.
/// </para>
/// <para>
/// The two sources are made as confusable as they can be: the same table names, the same column
/// names, the same identifiers and the same creators. Only the amounts and the notes differ, and
/// those are the canaries — a number or a token of one source reaching a principal entitled only in
/// the other is the leak, and the detector below scans the rows, the plan text and the disclosure
/// report of every statement for one.
/// </para>
/// <para>
/// <c>07_three_sources</c> stands in for design 45's three-source case with two sources and three
/// tables. The third source — a <em>remote</em> one, in a real database — is
/// <see cref="NamespaceRemoteTests"/>, alongside this file rather than folded into it: what only
/// that one can read is the SQL a source is <em>sent</em>, which is the one channel a policy's
/// predicate leaves Chalk through, and a POCO table has no source to push into.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class NamespaceConfusionTests(SharedSidecar sidecar)
{
    private static readonly NamespaceFixture Fixture = NamespaceFixture.Shared;

    private static readonly DirectoryInfo Goldens =
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-namespace"));

    /// <summary>One statement of the family: the name its golden takes, and the SQL.</summary>
    private sealed record Statement(string Name, string Sql);

    /// <summary>
    /// The family, in the order the goldens read best: each source's orders alone, the two joined,
    /// the unqualified name that has to pick one of them, each source's lines, and the three-table
    /// statement that meets both sources and one source twice.
    /// </summary>
    private static readonly IReadOnlyList<Statement> Family =
    [
        new("01_ledger_orders", "SELECT id, org_id, amount, note FROM ledger.orders ORDER BY id"),
        new("02_archive_orders", "SELECT id, org_id, amount, note FROM archive.orders ORDER BY id"),
        new(
            "03_the_join",
            "SELECT l.id, l.amount, a.amount FROM ledger.orders l "
            + "JOIN archive.orders a ON a.id = l.id ORDER BY l.id"),
        new("04_unqualified_orders", "SELECT id, amount FROM orders ORDER BY id"),
        new(
            "05_ledger_order_items",
            "SELECT id, order_id, quantity FROM ledger.order_items ORDER BY id"),
        new(
            "06_archive_order_items",
            "SELECT id, order_id, quantity FROM archive.order_items ORDER BY id"),
        new(
            "07_three_sources",
            "SELECT l.id, l.amount, a.amount, i.quantity FROM ledger.orders l "
            + "JOIN archive.orders a ON a.id = l.id "
            + "JOIN ledger.order_items i ON i.order_id = l.id ORDER BY l.id, i.id"),
    ];

    public static TheoryData<string> Statements()
    {
        var data = new TheoryData<string>();
        foreach (var statement in Family)
        {
            data.Add(statement.Name);
        }

        return data;
    }

    public static TheoryData<string> Principals()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in NamespaceFixture.Principals)
        {
            data.Add(name);
        }

        return data;
    }

    // ---------------------------------------------------------------- the goldens

    /// <summary>
    /// Every statement as every principal, recorded in the shape the tenancy family records: one
    /// block per principal, the disclosure report, then the rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The report names each table as <c>schema.table</c> rather than by its bare name, which the
    /// tenancy family had no need of and this one cannot do without: both sources hold a table called
    /// <c>orders</c>, and a record that could not say which was read would record the very confusion
    /// the family is about.
    /// </para>
    /// <para>
    /// The golden is a text a reviewer reads, and that is the whole of its value here: what a
    /// principal entitled in one source receives of the other is visible on the page, and a
    /// regression that widened it would show up as rows appearing under a block where there were
    /// none. Regenerate with <c>CHALK_WRITE_FIXTURES=1</c> and review the diff.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task The_family_records_what_each_principal_receives(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var statement = Named(name);
        var recorded = new StringBuilder();

        await using var engine = await EngineAsync();
        var entitled = engine.WithEntitlements();

        foreach (var (principal, grants) in NamespaceFixture.Principals)
        {
            recorded.Append("-- ").AppendLine(principal);

            EntitledQuery prepared;
            try
            {
                prepared = await entitled.PrepareAsync(
                    statement.Sql, Fixture.Entitlements.Bind(grants));
            }
            catch (EntitlementException refused)
            {
                // A refusal is an answer, and it is recorded as one (the tenancy family's own
                // shape). The detector reads every word of it, because a refusal that quoted a
                // value would be a leak through the message.
                Leaks.For(principal).Inspect(statement.Name, [], "", refused.Message);
                recorded.Append("POLICY ").AppendLine(FirstSentence(refused.Message));
                recorded.AppendLine();
                continue;
            }

            var report = Report(prepared);
            var rows = await RowsAsync(engine, prepared);

            recorded.Append("report ").AppendLine(report);
            foreach (var row in rows)
            {
                recorded.AppendLine(row);
            }

            recorded.AppendLine();

            Leaks.For(principal).Inspect(
                statement.Name, rows, Chalk.Ir.PlanPrinter.Print(prepared.Plan), report);

            // And the policy's own answer beside the golden, from the grants alone with no plan
            // involved, exactly as the tenancy corpus takes it (F89). It is worth taking here for a
            // reason that family has no way to state: every read of this one names a schema two
            // sources share, so a prediction that agreed by resolving the wrong source's model
            // would be agreeing about the wrong table.
            var reconciled = Fixture.Entitlements.Reconcile(prepared, grants);
            Assert.Empty(reconciled.Differences);
            Assert.DoesNotContain(
                reconciled.Visibility, v => v.Verdict == ReconciledVisibility.Disagrees);
            Assert.True(reconciled.Agrees);
        }

        await AssertGoldenAsync(name, recorded.ToString());
    }

    // ---------------------------------------------------------------- the two orders

    /// <summary>
    /// Each <c>orders</c> leaf was compiled under <b>its own</b> source's descriptor: the report says
    /// which, the plan text carries the same hash, and neither statement's plan carries the other's.
    /// </summary>
    /// <remarks>
    /// The descriptor hash is in the plan digest (D231), so two leaves compiled under two descriptors
    /// are two plans by construction. That is what makes this assertion worth making of a pair of
    /// tables that share a name: if the two hashes were equal, one policy would be answering for both
    /// sources, and every other assertion in this file would be reading one source twice.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task Each_orders_leaf_carries_its_own_source_s_descriptor(string principal)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();

        var (ledger, ledgerRefusal) = await TryPrepareAsync(engine, "01_ledger_orders", principal);
        if (ledgerRefusal is not null)
        {
            // The auditor's `amount` is population-only in the ledger and nowhere else, so the
            // refusal has to name the ledger's table. A refusal quoting the archive's would be the
            // confusion this family is about, arriving through the message rather than through a row.
            Assert.Contains(
                $"{NamespaceFixture.Ledger}.orders.amount",
                ledgerRefusal.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"{NamespaceFixture.Archive}.orders", ledgerRefusal.Message, StringComparison.Ordinal);
            return;
        }

        var archive = await PrepareAsync(engine, "02_archive_orders", principal);

        var ledgerTable = Assert.Single(ledger!.Entitlements.Tables);
        var archiveTable = Assert.Single(archive.Entitlements.Tables);

        Assert.Equal("orders", ledgerTable.Table);
        Assert.Equal("orders", archiveTable.Table);
        Assert.Equal(NamespaceFixture.Ledger, ledgerTable.Schema);
        Assert.Equal(NamespaceFixture.Archive, archiveTable.Schema);

        // The hash the report names is the one the policy compiled for that source, and not the one
        // it compiled for the other.
        Assert.Equal(
            Descriptor(NamespaceFixture.Ledger, "orders").DescriptorHash, ledgerTable.DescriptorHash);
        Assert.Equal(
            Descriptor(NamespaceFixture.Archive, "orders").DescriptorHash, archiveTable.DescriptorHash);
        Assert.NotEqual(ledgerTable.DescriptorHash, archiveTable.DescriptorHash);

        // And the leaf in the plan says the same thing, which is what the digest covers.
        var ledgerPlan = Chalk.Ir.PlanPrinter.Print(ledger.Plan);
        var archivePlan = Chalk.Ir.PlanPrinter.Print(archive.Plan);
        Assert.Contains(
            $"descriptor={ledgerTable.DescriptorHash}", ledgerPlan, StringComparison.Ordinal);
        Assert.Contains(
            $"descriptor={archiveTable.DescriptorHash}", archivePlan, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"descriptor={archiveTable.DescriptorHash}", ledgerPlan, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"descriptor={ledgerTable.DescriptorHash}", archivePlan, StringComparison.Ordinal);
    }

    /// <summary>
    /// A principal entitled in one source reads that source's orders and none of the other's, in both
    /// directions.
    /// </summary>
    /// <remarks>
    /// Neither principal owns a row anywhere — the creators are 100 to 103 — so the only way into
    /// either table is the grant, and the grant reaches one source's dimension and nothing of the
    /// other's. Both directions are asserted because a bug that resolved every <c>orders</c> to the
    /// first source would leave the first direction passing.
    /// </remarks>
    [Fact]
    public async Task A_principal_entitled_in_one_source_reads_only_that_source_s_orders()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();

        Assert.NotEmpty(await RowsAsync(engine, "01_ledger_orders", "ledger-only"));
        Assert.Empty(await RowsAsync(engine, "02_archive_orders", "ledger-only"));

        Assert.NotEmpty(await RowsAsync(engine, "02_archive_orders", "archive-only"));
        Assert.Empty(await RowsAsync(engine, "01_ledger_orders", "archive-only"));
    }

    // ---------------------------------------------------------------- the join

    /// <summary>
    /// The two <c>orders</c> joined: the report names two tables, each carries its schema, and the
    /// two <c>amount</c> columns are disclosed differently.
    /// </summary>
    /// <remarks>
    /// A report that named the tables by their bare names would say <c>orders</c> twice and a reader
    /// could not tell which row of it spoke for which leaf; the schema is what makes it readable and
    /// what makes it checkable. The two amounts are the other half: <c>global</c> holds a manager's
    /// grant everywhere, and the two sources' rules give a manager different things — the ledger's
    /// amount in full, the archive's masked unless the reader owns the row. Two identical labels here
    /// would mean one descriptor had answered for both leaves.
    /// </remarks>
    [Fact]
    public async Task The_join_names_both_tables_by_schema_and_discloses_the_two_amounts_apart()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        var prepared = await PrepareAsync(engine, "03_the_join", "global");

        Assert.Equal(2, prepared.Entitlements.Tables.Count);
        Assert.Contains(
            prepared.Entitlements.Tables,
            t => t.Schema == NamespaceFixture.Ledger && t.Table == "orders");
        Assert.Contains(
            prepared.Entitlements.Tables,
            t => t.Schema == NamespaceFixture.Archive && t.Table == "orders");
        Assert.All(prepared.Entitlements.Tables, t => Assert.NotEqual("", t.Schema));

        // The projection is l.id, l.amount, a.amount, so the two amounts are the second and third
        // outputs. By position, because the two carry the same name.
        Assert.Equal(3, prepared.Columns.Count);
        Assert.NotEqual(prepared.Columns[1].Disclosure, prepared.Columns[2].Disclosure);
    }

    // ---------------------------------------------------------------- the unqualified name

    /// <summary>
    /// An unqualified <c>orders</c> resolves in the default schema — the catalog's first — and in
    /// exactly one table, for every principal.
    /// </summary>
    /// <remarks>
    /// The default schema is the first schema of the context (<c>docs/design/03-planner.md</c> §2),
    /// which here is the ledger. What is worth asserting is not only <em>which</em> it picked but
    /// that it picked <em>one</em>: a statement that named an ambiguous table and was answered with
    /// both would disclose the archive to a principal who asked for nothing of it, and the report is
    /// where that would first be visible.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task An_unqualified_orders_resolves_in_the_default_schema(string principal)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        var (prepared, refusal) = await TryPrepareAsync(engine, "04_unqualified_orders", principal);
        if (refusal is not null)
        {
            // An unqualified name is the default schema's, so even the refusal says which source it
            // is about — which is the claim this test makes, reached the other way round.
            Assert.Contains(
                $"{NamespaceFixture.Ledger}.orders.amount", refusal.Message, StringComparison.Ordinal);
            return;
        }

        var table = Assert.Single(prepared!.Entitlements.Tables);
        Assert.Equal("orders", table.Table);
        Assert.Equal(NamespaceFixture.Ledger, table.Schema);
        Assert.Equal(
            Descriptor(NamespaceFixture.Ledger, "orders").DescriptorHash, table.DescriptorHash);
    }

    // ---------------------------------------------------------------- the two paths

    /// <summary>
    /// Each <c>order_items</c> inherits its tenancy along a path that ends at <b>its own</b> source's
    /// <c>orders</c>, and the explanation names the schema at every step.
    /// </summary>
    /// <remarks>
    /// Both paths end at a table called <c>orders</c>, so the endpoint's name says nothing at all;
    /// the schema is the only thing that distinguishes them, which is exactly why a <c>Table</c>
    /// handle has to carry the source it came from. The explanation is the oracle a host reads when a
    /// row is missing (D207), so it is also where a route through the wrong source would be seen
    /// first — before any row was fetched.
    /// </remarks>
    [Theory]
    [InlineData("05_ledger_order_items", NamespaceFixture.Ledger, "org", "ledger-only")]
    [InlineData("06_archive_order_items", NamespaceFixture.Archive, "region", "archive-only")]
    public async Task The_path_of_each_order_items_stays_in_its_own_source(
        string name, string schema, string kind, string principal)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        var prepared = await PrepareAsync(engine, name, principal);
        var explained = await prepared.ExplainAsync();

        var table = Assert.Single(explained.Tables, t => t.Table == "order_items");
        Assert.Equal(schema, table.Schema);

        var path = Assert.Single(table.Paths);
        Assert.Equal(kind, path.Kind);
        Assert.Equal("orders", path.EndpointTable);
        Assert.Equal(schema, path.EndpointSchema);

        var step = Assert.Single(path.Steps);
        Assert.Equal("orders", step.Table);
        Assert.Equal(schema, step.Schema);
        Assert.Equal("order_id", step.FromColumn);
        Assert.Equal("id", step.ToColumn);
    }

    /// <summary>
    /// The archive's principal reads the archive's lines and no line of the ledger, although the two
    /// tables share a name, a shape and their orders' identifiers.
    /// </summary>
    /// <remarks>
    /// Neither <c>order_items</c> declares a resource owner or any restriction but its one inherited
    /// path, so a row of the ledger's reaching a principal who holds only a region grant could have
    /// arrived by exactly one route: the archive's path answered for the ledger's table.
    /// </remarks>
    [Fact]
    public async Task The_archive_s_principal_reads_no_line_of_the_ledger()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();

        Assert.NotEmpty(await RowsAsync(engine, "06_archive_order_items", "archive-only"));
        Assert.Empty(await RowsAsync(engine, "05_ledger_order_items", "archive-only"));
    }

    // ---------------------------------------------------------------- the oracle

    /// <summary>
    /// What each principal should read of each source's orders, stated in C# from the rows and the
    /// declarations and nothing else — no descriptor, no context, no plan.
    /// </summary>
    /// <remarks>
    /// The oracle is naive on purpose, and that is the whole of its value: it is the policy read a
    /// second way, by a reader who knows only the four sentences the fixture declares. A compiler
    /// that resolved the wrong source's rules would still produce a plan that runs and a report that
    /// reads plausibly; what it could not do is agree with a disjunction written out by hand.
    /// The identifier and the note are compared because <c>note</c> is in no realm and named by no
    /// rule, so it is disclosed in full to anyone who can see the row at all — which makes it the one
    /// column that says, per row, which table the row came from.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task The_oracle_and_the_engine_agree_on_the_orders_each_principal_reads(
        string principal)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        var held = Oracle.Held(principal);

        var (ledger, refusal) = await TryPrepareAsync(engine, "01_ledger_orders", principal);
        if (refusal is not null)
        {
            // The one principal these two statements refuse is the ledger's auditor, whose `amount`
            // is population-only there. The oracle states rows, not refusals, so this principal's
            // rows are the family's own record; what is asserted here is that the refusal belongs to
            // the source whose rule made it.
            Assert.Contains(
                $"{NamespaceFixture.Ledger}.orders.amount", refusal.Message, StringComparison.Ordinal);
            return;
        }

        Assert.Equal(
            NamespaceFixture.LedgerOrders
                .Where(o => Oracle.SeesLedgerOrder(held, o))
                .Select(o => $"{o.Id}|{o.Note}"),
            IdAndNote(await RowsAsync(engine, ledger!)));

        Assert.Equal(
            NamespaceFixture.ArchiveOrders
                .Where(o => Oracle.SeesArchiveOrder(held, o))
                .Select(o => $"{o.Id}|{o.Note}"),
            IdAndNote(await RowsAsync(engine, "02_archive_orders", principal)));
    }

    /// <summary>
    /// The same for the two <c>order_items</c>, where every selected column is disclosed in full to
    /// anyone who can see the row, so the whole row is compared.
    /// </summary>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task The_oracle_and_the_engine_agree_on_the_lines_each_principal_reads(
        string principal)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        var held = Oracle.Held(principal);

        Assert.Equal(
            NamespaceFixture.LedgerOrderItems
                .Where(i => Oracle.SeesLedgerLine(held, i))
                .Select(i => $"{i.Id}|{i.OrderId}|{i.Quantity}"),
            await RowsAsync(engine, "05_ledger_order_items", principal));

        Assert.Equal(
            NamespaceFixture.ArchiveOrderItems
                .Where(i => Oracle.SeesArchiveLine(held, i))
                .Select(i => $"{i.Id}|{i.OrderId}|{i.Quantity}"),
            await RowsAsync(engine, "06_archive_order_items", principal));
    }

    /// <summary>
    /// The policy as a reader of the declarations would state it, and the grants as the fixture
    /// hands them out.
    /// </summary>
    private static class Oracle
    {
        /// <summary>
        /// What one principal holds. Nulls are "no grant of that kind", which is what makes the
        /// disjunctions below read as the declarations do.
        /// </summary>
        internal sealed record Holding(
            int User,
            bool Global = false,
            int? Org = null,
            int? Region = null,
            int? Member = null,
            int? MemberInOrg = null);

        /// <summary>The grants of <see cref="NamespaceFixture.Principals"/>, restated by hand.</summary>
        internal static Holding Held(string principal) => principal switch
        {
            "ledger-only" => new Holding(1, Org: 1),
            "archive-only" => new Holding(2, Region: 7),
            "both" => new Holding(3, Org: 1, Region: 7),
            "global" => new Holding(4, Global: true),
            "owner" => new Holding(100),
            "subject" => new Holding(5, Member: 11, MemberInOrg: 1),
            "auditor-in-ledger" => new Holding(6, Org: 1),
            _ => throw new ArgumentOutOfRangeException(
                nameof(principal), principal, "no such principal"),
        };

        /// <summary>
        /// <c>ledger.orders</c> is restricted by its organisation, by the member it is about and by
        /// its creator, OR-ed — and a subject grant reaches a row only where its confining
        /// organisation resolves there too (D266 §3).
        /// </summary>
        internal static bool SeesLedgerOrder(Holding held, NamespaceFixture.Order order) =>
            held.Global
            || order.OrgId == held.Org
            || (order.MemberId == held.Member && order.OrgId == held.MemberInOrg)
            || order.CreatedBy == held.User;

        /// <summary>
        /// <c>archive.orders</c> is restricted by its region and by its creator, and by nothing else:
        /// an organisation grant reaches no row of it, whatever the row's <c>org_id</c> says.
        /// </summary>
        internal static bool SeesArchiveOrder(Holding held, NamespaceFixture.Order order) =>
            held.Global || order.RegionId == held.Region || order.CreatedBy == held.User;

        /// <summary>
        /// <c>ledger.order_items</c> holds no tenancy of its own: a line is visible when its own
        /// source's order is reached by the organisation grant.
        /// </summary>
        internal static bool SeesLedgerLine(Holding held, NamespaceFixture.OrderItem line) =>
            held.Global
            || NamespaceFixture.LedgerOrders.Any(o => o.Id == line.OrderId && o.OrgId == held.Org);

        /// <summary>The same for the archive's lines, along the region.</summary>
        internal static bool SeesArchiveLine(Holding held, NamespaceFixture.OrderItem line) =>
            held.Global
            || NamespaceFixture.ArchiveOrders.Any(
                o => o.Id == line.OrderId && o.RegionId == held.Region);
    }

    // ---------------------------------------------------------------- the leak detector

    /// <summary>
    /// Everything a principal receives, scanned for a canary of the source they are not entitled in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This family's own detector rather than <see cref="LeakDetector"/>, which is bound to the
    /// tenancy fixture's oracle and its canary vocabulary: here the forbidden set is not "what this
    /// principal's rules withhold" but the simpler and stricter "everything the other source holds",
    /// and it is that simplicity which makes a failure legible — a token or an amount of a source a
    /// principal holds no grant in has no route to them at all, so its appearance is never a question
    /// of degree.
    /// </para>
    /// <para>
    /// "Receive" is broad on purpose: the result rows, the plan text and the disclosure report. It
    /// fails through <c>Assert.Fail</c> naming the principal, the token and where it appeared,
    /// because what leaked, to whom and through which channel is the whole of the report.
    /// </para>
    /// </remarks>
    private sealed class Leaks
    {
        private static readonly IReadOnlySet<string> NoTokens =
            new HashSet<string>(StringComparer.Ordinal);

        private readonly string _principal;
        private readonly IReadOnlySet<string> _tokens;
        private readonly IReadOnlyList<long> _amounts;

        private Leaks(string principal, IReadOnlySet<string> tokens, IReadOnlyList<long> amounts)
        {
            _principal = principal;
            _tokens = tokens;
            _amounts = amounts;
        }

        /// <summary>
        /// What this principal may not receive. A principal whose every grant is in one source is
        /// forbidden the whole of the other; a principal entitled in both, or one who owns a row of
        /// each, is forbidden nothing by source and is scanned against an empty set, which is honest
        /// rather than vacuous — the goldens and the oracle are what hold those three.
        /// </summary>
        internal static Leaks For(string principal) => principal switch
        {
            "ledger-only" or "subject" or "auditor-in-ledger" => new Leaks(
                principal, NamespaceFixture.ArchiveCanaries, NamespaceFixture.ArchiveAmounts),
            "archive-only" => new Leaks(
                principal, NamespaceFixture.LedgerCanaries, NamespaceFixture.LedgerAmounts),
            "both" or "global" or "owner" => new Leaks(principal, NoTokens, []),
            _ => throw new ArgumentOutOfRangeException(
                nameof(principal), principal, "no such principal"),
        };

        internal void Inspect(
            string statement, IReadOnlyList<string> rows, string planText, string report)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                Scan(statement, $"row {i + 1}", rows[i]);
            }

            Scan(statement, "the plan text", planText);
            Scan(statement, "the disclosure report", report);
        }

        private void Scan(string statement, string where, string text)
        {
            foreach (var token in _tokens)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"{statement}: {_principal} received the canary {token} in {where}, and holds "
                        + "no grant in the source that token belongs to. The value is: "
                        + Quote(text));
                }
            }

            foreach (var amount in _amounts)
            {
                if (Holds(text, amount))
                {
                    Assert.Fail(
                        $"{statement}: {_principal} received the canary amount {amount} in {where}, "
                        + "and holds no grant in the source that amount belongs to. The value is: "
                        + Quote(text));
                }
            }
        }

        /// <summary>
        /// Whether the text holds this number as a value rather than inside a longer run of letters
        /// and digits.
        /// </summary>
        /// <remarks>
        /// A plan text carries hexadecimal digests and descriptor hashes, and four digits that happen
        /// to fall inside one of those are not a value that escaped: a leaked amount stands between
        /// separators, in a cell or in a folded literal. So the boundary excludes letters as well as
        /// digits, which costs nothing a real leak would have needed.
        /// </remarks>
        private static bool Holds(string text, long amount)
        {
            var digits = amount.ToString(CultureInfo.InvariantCulture);
            var at = text.IndexOf(digits, StringComparison.Ordinal);
            while (at >= 0)
            {
                var end = at + digits.Length;
                var before = at == 0 || !char.IsAsciiLetterOrDigit(text[at - 1]);
                var after = end == text.Length || !char.IsAsciiLetterOrDigit(text[end]);
                if (before && after)
                {
                    return true;
                }

                at = text.IndexOf(digits, at + 1, StringComparison.Ordinal);
            }

            return false;
        }

        private static string Quote(string text) => text.Length <= 400 ? text : text[..400] + "…";
    }

    // ---------------------------------------------------------------- helpers

    private static Statement Named(string name) => Family.Single(s => s.Name == name);

    private static TenancyPrincipal PrincipalNamed(string name)
    {
        foreach (var (declared, principal) in NamespaceFixture.Principals)
        {
            if (string.Equals(declared, name, StringComparison.Ordinal))
            {
                return principal;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "no such principal");
    }

    /// <summary>
    /// The descriptor the policy compiled for one source's table, asked for the way D270 makes a host
    /// ask: by the schema name and the table name together.
    /// </summary>
    /// <remarks>
    /// Never null here: both <c>orders</c> are restricted, so the policy compiled a descriptor for
    /// each, and a null would mean the lookup had missed the source rather than the table.
    /// </remarks>
    private static TableEntitlementDescriptor Descriptor(string schema, string table) =>
        Fixture.Entitlements.For(schema, table)!;

    /// <summary>
    /// The per-column disclosures and the per-table visibilities, in the tenancy family's shape with
    /// each table named by its schema — which is what tells the two <c>orders</c> apart.
    /// </summary>
    private static string Report(EntitledQuery prepared) =>
        string.Join(
            ", ",
            prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                .Concat(prepared.Entitlements.Tables.Select(
                    t => $"{t.Schema}.{t.Table}:{t.Visibility}")));

    /// <summary>The identifier and the note of each row, which is what the oracle states.</summary>
    private static IEnumerable<string> IdAndNote(IEnumerable<string> rows) =>
        rows.Select(row => row.Split('|')).Select(cells => $"{cells[0]}|{cells[3]}");

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = NamespaceFixture.ContextId,
            Sources = [Fixture.LedgerSource, Fixture.ArchiveSource],
            Planner = sidecar.CreatePlanner(),
        });

    private static async Task<EntitledQuery> PrepareAsync(
        ChalkEngine engine, string statement, string principal) =>
        await engine.WithEntitlements().PrepareAsync(
            Named(statement).Sql, Fixture.Entitlements.Bind(PrincipalNamed(principal)));

    /// <summary>
    /// The prepared statement, or the refusal the policy answered with.
    /// </summary>
    /// <remarks>
    /// One principal of this family is an auditor whose <c>ledger.orders.amount</c> is
    /// population-only, and four of the seven statements project that column bare. The refusal is the
    /// right answer and is itself a namespace assertion: the message names <em>which</em>
    /// <c>orders</c> the column belongs to, so a refusal quoting the archive's would be the confusion
    /// this family exists to catch.
    /// </remarks>
    private static async Task<(EntitledQuery? Prepared, EntitlementException? Refusal)>
        TryPrepareAsync(ChalkEngine engine, string statement, string principal)
    {
        try
        {
            return (await PrepareAsync(engine, statement, principal), null);
        }
        catch (EntitlementException refused)
        {
            return (null, refused);
        }
    }

    /// <summary>The first sentence of a refusal, which is what a golden records of one.</summary>
    private static string FirstSentence(string message)
    {
        var stop = message.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? message : message[..(stop + 1)];
    }

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine, string statement, string principal) =>
        await RowsAsync(engine, await PrepareAsync(engine, statement, principal));

    private static async Task<string[]> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
            }
        }

        return [.. rows];
    }

    private static async Task AssertGoldenAsync(string name, string recorded)
    {
        var golden = new FileInfo(Path.Combine(Goldens.FullName, name + ".txt"));
        if (Environment.GetEnvironmentVariable("CHALK_WRITE_FIXTURES") == "1")
        {
            Goldens.Create();
            await File.WriteAllTextAsync(golden.FullName, recorded);
            return;
        }

        Assert.True(
            golden.Exists,
            $"{golden.FullName} does not exist. Regenerate with CHALK_WRITE_FIXTURES=1 and review it.");
        Assert.Equal(
            (await File.ReadAllTextAsync(golden.FullName)).ReplaceLineEndings("\n"),
            recorded.ReplaceLineEndings("\n"));
    }
}
