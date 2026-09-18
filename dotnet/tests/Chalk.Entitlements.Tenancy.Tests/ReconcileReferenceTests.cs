using Chalk.Catalog;
using Chalk.Entitlements.Tenancy;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// Which table of which source a reconciliation reads a plan's table reference as (F89, ADR 0056
/// §7.7).
/// </summary>
/// <remarks>
/// <para>
/// A plan's <c>Read</c> names the schema the statement resolved in, and so does the report's
/// <c>EntitledTable</c>; the compiled models are keyed by <c>schema.table</c>. What this file holds
/// is the three answers that follow: a qualified reference is answered by <b>its own</b> source's
/// model and never by another's, a reference that names no schema is answered by the bare-name index
/// where the name is one no two sources share, and one that names no schema and a name they do share
/// is <b>refused</b> — the two models disagree about what the read must disclose, so choosing
/// between them would be a guess dressed as an answer.
/// </para>
/// <para>
/// The plans here are built by hand rather than planned, because what is under test is the lookup
/// and not the pass: a two-line <c>Read</c> says exactly which reference is being resolved, and a
/// planned one would say it only incidentally. A real planner names the schema on every read and on
/// every report entry, so the refusal below is unreachable from a planned statement — which is the
/// point of asserting it here, where a reference that names none can be written.
/// </para>
/// </remarks>
public sealed class ReconcileReferenceTests
{
    private static readonly NamespaceFixture Namespaces = NamespaceFixture.Shared;

    /// <summary>The ordinal of <c>amount</c> in both sources' <c>orders</c>, which share a row type.</summary>
    private const int Amount = 4;

    private const int Columns = 7;

    /// <summary>
    /// The two sources give <c>amount</c> different rules, so what the policy expects of one
    /// source's read is not what it expects of the other's. Reading the same claim under the two
    /// schemas and finding one answer would mean one model had answered for both leaves, which is
    /// the confusion the whole namespace family is about — arriving here through the reconciliation
    /// rather than through a row.
    /// </summary>
    /// <remarks>
    /// The three principals are ones the two sources answer <em>differently</em>: a manager in the
    /// ledger's organisation, a manager in the archive's region, and the ledger's auditor, whose
    /// amount is population-only there and masked here. A principal the two happen to answer alike
    /// would make the assertion vacuous, which is why the list is named rather than taken whole.
    /// </remarks>
    [Theory]
    [InlineData("ledger-only")]
    [InlineData("archive-only")]
    [InlineData("auditor-in-ledger")]
    public void A_qualified_reference_is_answered_by_its_own_source_s_rules(string principal)
    {
        var grants = Principal(principal);
        var ledger = Expected(
            Namespaces.Entitlements, NamespaceFixture.Ledger, "orders", grants);
        var archive = Expected(
            Namespaces.Entitlements, NamespaceFixture.Archive, "orders", grants);

        Assert.NotEqual(ledger, archive);
    }

    /// <summary>
    /// A read of a table this policy governs in <em>another</em> source resolves to nothing at all —
    /// and emphatically not to the table of that name it does govern.
    /// </summary>
    /// <remarks>
    /// The reproduction, and the shape a host has the moment two of its sources hold a table of one
    /// name and it entitles one of them: a catalog with a <c>ledger.orders</c> and an
    /// <c>archive.orders</c>, and a policy that speaks about the ledger's alone. The bare-name index
    /// then holds <c>orders</c>, because among the tables the policy governs the name is
    /// unambiguous, and the archive's schema does hold a table to describe — so before this every
    /// read of the archive's <c>orders</c> was answered by the ledger's rules, which decide what the
    /// read must disclose. The honest answer is silence: the policy has said nothing about that
    /// table.
    /// </remarks>
    [Fact]
    public void A_read_of_a_table_governed_in_another_source_resolves_to_nothing()
    {
        var entitlements = OneSourceOfTwo();
        var grants = NamespaceFixture.Principal(
            1, Grant.ForTenancy(NamespaceFixture.Org, 1, NamespaceFixture.Manager));

        Assert.NotEmpty(
            entitlements.Reconcile(PlanOf(NamespaceFixture.Ledger, "orders", Columns), grants));
        Assert.Empty(
            entitlements.Reconcile(PlanOf(NamespaceFixture.Archive, "orders", Columns), grants));
    }

    /// <summary>A bare name no two sources share still resolves, which is what ADR 0056 §7.7 kept.</summary>
    [Fact]
    public void A_bare_name_no_two_sources_share_still_resolves()
    {
        var entitlements = OneSourceOfTwo();
        var grants = NamespaceFixture.Principal(
            1, Grant.ForTenancy(NamespaceFixture.Org, 1, NamespaceFixture.Manager));

        var qualified = entitlements
            .Reconcile(PlanOf(NamespaceFixture.Ledger, "orders", Columns), grants)
            .Select(difference => difference.ToString());

        Assert.NotEmpty(qualified);
        Assert.Equal(
            qualified,
            entitlements.Reconcile(PlanOf("", "orders", Columns), grants)
                .Select(difference => difference.ToString()));
    }

    /// <summary>
    /// And a bare name two sources <em>do</em> share is refused, naming the table and both of them.
    /// </summary>
    /// <remarks>
    /// Refused rather than skipped: a reconciliation that silently said nothing about a table would
    /// read exactly like one that found nothing to disagree with, and the corpus's own "no
    /// differences" would be vacuous for that table. Refusing says which reference could not be
    /// read.
    /// </remarks>
    [Fact]
    public void A_bare_name_two_sources_share_is_refused_by_name()
    {
        var grants = Principal("both");

        var refusal = Assert.Throws<CatalogValidationException>(
            () => Namespaces.Entitlements.Reconcile(PlanOf("", "orders", Columns), grants));

        Assert.Contains("'orders'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"'{NamespaceFixture.Archive}.orders'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"'{NamespaceFixture.Ledger}.orders'", refusal.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A catalog of the namespace fixture's two schemas, and a policy that governs the ledger's
    /// <c>orders</c> and says nothing at all about the archive's.
    /// </summary>
    private static TenancyEntitlements OneSourceOfTwo()
    {
        var catalog = new Chalk.Catalog.CatalogContext
        {
            ContextId = NamespaceFixture.ContextId,
            Epoch = NamespaceFixture.Epoch,
            Schemas = [NamespaceFixture.LedgerSchema, NamespaceFixture.ArchiveSchema],
        };

        var policy = TenancyPolicy.Declare(catalog);
        var org = policy.Tenancy("org");
        var manager = policy.Role("manager");
        var orders = policy.Source(NamespaceFixture.Ledger).Table("orders");

        orders
            .Tenancy(t => t
                .Direct(org, orders.Column("org_id"))
                .ResourceOwner(orders.Column("created_by")))
            .Access(new AccessRule
            {
                Roles = [manager],
                Column = orders.Column("amount"),
                Grants = Verdict.Full,
            });

        return policy.Compile(catalog);
    }

    private static TenancyPrincipal Principal(string name)
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
    /// What the policy expects of every column of one source's table, read off the differences it
    /// reports against a read that claims every column in full. A column the policy agrees is
    /// <c>Full</c> raises no difference, so the claim itself is the answer for it.
    /// </summary>
    private static IReadOnlyList<DisclosureOutcome> Expected(
        TenancyEntitlements entitlements, string schema, string table, TenancyPrincipal principal)
    {
        var outcomes = new DisclosureOutcome[Columns];
        Array.Fill(outcomes, DisclosureOutcome.Full);
        foreach (var difference in entitlements.Reconcile(PlanOf(schema, table, Columns), principal))
        {
            outcomes[Ordinal(difference.Column)] = difference.Expected;
        }

        return outcomes;
    }

    private static int Ordinal(string column) => column switch
    {
        "id" => 0,
        "org_id" => 1,
        "member_id" => 2,
        "region_id" => 3,
        "amount" => Amount,
        "created_by" => 5,
        _ => 6,
    };

    /// <summary>
    /// A plan of one read of one table, claiming every column in full — the simplest plan that
    /// carries a table reference and a verdict for each of its columns.
    /// </summary>
    private static Plan PlanOf(string schema, string table, int columns)
    {
        var read = new Read
        {
            Table = new TableRef { SourceId = "", Schema = schema, Table = table },
        };

        for (var i = 0; i < columns; i++)
        {
            read.Projection.Add((uint)i);
            read.Disclosures.Add(new ColumnDisclosure
            {
                Column = (uint)i,
                Outcome = DisclosureOutcome.Full,
            });
        }

        return new Plan { Root = new Rel { Read = read } };
    }
}
